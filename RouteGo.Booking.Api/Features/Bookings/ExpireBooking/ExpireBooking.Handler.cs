namespace RouteGo.Booking.Api.Features.Bookings.ExpireBooking;

/// <summary>
/// Expires an unpaid booking whose hold Seat Inventory let go of without Booking asking: the hold
/// timer ran out or an admin freed it. Releases Booking caused itself change nothing. The move is
/// conditional on the booking still waiting for payment, so a repeat or a payment that got there
/// first leaves the booking alone, and BookingExpired is recorded with the change.
/// </summary>
public sealed class ExpireBookingHandler : IEventWorker
{
    private const string AuditAction = "ExpireBooking";
    private const string SeatReleasedType = "SeatReleased";
    private const string HoldExpiredReason = "HoldExpired";
    private const string AdminReleasedReason = "AdminReleased";

    // Releases Booking caused itself: it already knows what its own request did.
    private static readonly string[] _bookingCausedReasons = { "HoldReleased", "BookingFailed", "BookingCancelled" };

    private static readonly string[] _waitingStatuses =
    {
        nameof(BookingStatus.SeatsHeld),
        nameof(BookingStatus.PaymentPending),
    };

    private const string SelectStatusSql = """
        SELECT status FROM bookings.bookings WHERE booking_id = @BookingId FOR UPDATE
        """;

    private const string ExpireSql = """
        UPDATE bookings.bookings
        SET status = 'Expired'
        WHERE booking_id = @BookingId AND status = ANY(@WaitingStatuses)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly ILogger<ExpireBookingHandler> _logger;

    public ExpireBookingHandler(IWriterConnectionFactory writerFactory, IClock clock, ILogger<ExpireBookingHandler> logger)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EventType
    {
        get { return SeatReleasedType; }
    }

    /// <summary>
    /// Applies one SeatReleased event. Applied covers an expired booking and every release that
    /// changes nothing; Rejected is a release for an unknown booking or with unusable data.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        SeatReleasedData? data = TryRead(receivedEvent.Data);
        bool hasBooking = Guid.TryParse(data?.BookingId, out Guid bookingId);
        bool hasTrip = Guid.TryParse(data?.TripId, out Guid _);
        bool hasReason = IsKnownReason(data?.Reason);
        if (!hasBooking || !hasTrip || !hasReason)
        {
            return EventOutcome.Rejected(
                $"SeatReleased event {receivedEvent.EventId} needs a tripId, a bookingId and a known reason (booking {data?.BookingId}, reason {data?.Reason})");
        }

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        string? status = await writer.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
            SelectStatusSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
        if (status is null)
        {
            return EventOutcome.Rejected(
                "bookingUnknown", $"event {receivedEvent.EventId}: Seat Inventory released seats of booking {bookingId}, which does not exist");
        }

        bool isExpiring = data!.Reason == HoldExpiredReason || data.Reason == AdminReleasedReason;
        if (isExpiring && Array.IndexOf(_waitingStatuses, status) >= 0)
        {
            await ExpireAsync(writer, bookingId, receivedEvent, cancellationToken);
        }

        return EventOutcome.Applied();
    }

    private async Task ExpireAsync(IWriterConnection writer, Guid bookingId, ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        var expiry = new { BookingId = bookingId, WaitingStatuses = _waitingStatuses };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            ExpireSql, expiry, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return;
        }

        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, bookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, receivedEvent.CorrelationId);
        await recorder.RecordExpiredAsync(context, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "Booking {BookingId} expired at {ExpiredAt}: its hold was released (correlation {CorrelationId})",
            bookingId, _clock.UtcNow, receivedEvent.CorrelationId);
    }

    private static bool IsKnownReason(string? reason)
    {
        if (reason is null)
        {
            return false;
        }

        bool isExpiring = reason == HoldExpiredReason || reason == AdminReleasedReason;
        return isExpiring || Array.IndexOf(_bookingCausedReasons, reason) >= 0;
    }

    // Data that cannot be read at all is unusable like any other; the handler names it.
    private static SeatReleasedData? TryRead(JsonElement data)
    {
        try
        {
            return data.Deserialize<SeatReleasedData>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
