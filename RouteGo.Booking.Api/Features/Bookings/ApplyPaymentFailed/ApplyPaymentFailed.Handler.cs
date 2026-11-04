namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentFailed;

/// <summary>
/// Ends a booking whose payment failed: a booking still waiting for the payment result becomes Failed
/// (PaymentDeclined), keeping Payment's reason, and BookingFailed is recorded with the change so Seat
/// Inventory frees the seats. A booking in any other status is left alone, which also makes a repeat harmless.
/// </summary>
public sealed class ApplyPaymentFailedHandler : IEventWorker
{
    private const string AuditAction = "ApplyPaymentFailed";
    private const string PaymentFailedType = "PaymentFailed";

    // The column holds at most 500 characters; a longer reason is cut rather than lost with the event.
    private const int MaxDetailLength = 500;

    private static readonly string[] _waitingStatuses =
    {
        nameof(BookingStatus.SeatsHeld),
        nameof(BookingStatus.PaymentPending),
    };

    private const string SelectStatusSql = """
        SELECT status FROM bookings.bookings WHERE booking_id = @BookingId FOR UPDATE
        """;

    private const string FailSql = """
        UPDATE bookings.bookings
        SET status = 'Failed', failure_reason = 'PaymentDeclined', payment_failure_detail = @Detail
        WHERE booking_id = @BookingId AND status = ANY(@WaitingStatuses)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly ILogger<ApplyPaymentFailedHandler> _logger;

    public ApplyPaymentFailedHandler(IWriterConnectionFactory writerFactory, IClock clock, ILogger<ApplyPaymentFailedHandler> logger)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EventType
    {
        get { return PaymentFailedType; }
    }

    /// <summary>
    /// Applies one PaymentFailed event. Applied covers a booking failed now and every failure that
    /// changes nothing; Rejected is a failure for an unknown booking or with unusable data.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        PaymentFailedData? data = TryRead(receivedEvent.Data);
        bool hasBooking = Guid.TryParse(data?.BookingId, out Guid bookingId);
        if (!hasBooking)
        {
            return EventOutcome.Rejected($"PaymentFailed event {receivedEvent.EventId} needs a bookingId (got {data?.BookingId})");
        }

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        string? status = await writer.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
            SelectStatusSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
        if (status is null)
        {
            return EventOutcome.Rejected(
                "bookingUnknown", $"event {receivedEvent.EventId}: Payment reports a failed payment for booking {bookingId}, which does not exist");
        }

        if (Array.IndexOf(_waitingStatuses, status) >= 0)
        {
            await FailAsync(writer, bookingId, data!.Reason, receivedEvent, cancellationToken);
        }

        return EventOutcome.Applied();
    }

    private async Task FailAsync(
        IWriterConnection writer, Guid bookingId, string? paymentReason, ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        var failure = new { BookingId = bookingId, Detail = Shorten(paymentReason), WaitingStatuses = _waitingStatuses };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            FailSql, failure, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return;
        }

        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, bookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, receivedEvent.CorrelationId);
        await recorder.RecordFailedAsync(context, FailureReason.PaymentDeclined, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "Booking {BookingId} failed at {FailedAt}: its payment was declined (correlation {CorrelationId})",
            bookingId, _clock.UtcNow, receivedEvent.CorrelationId);
    }

    private static string? Shorten(string? reason)
    {
        if (reason is null || reason.Length <= MaxDetailLength)
        {
            return reason;
        }

        return reason.Substring(0, MaxDetailLength);
    }

    // Data that cannot be read at all is unusable like any other; the handler names it.
    private static PaymentFailedData? TryRead(JsonElement data)
    {
        try
        {
            return data.Deserialize<PaymentFailedData>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
