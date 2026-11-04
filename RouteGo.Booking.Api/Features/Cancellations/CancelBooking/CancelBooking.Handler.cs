namespace RouteGo.Booking.Api.Features.Cancellations.CancelBooking;

/// <summary>
/// Cancels a confirmed booking for its customer. In one transaction: the booking becomes Cancelled, the
/// cancellation is recorded with the refund share worked out now, a refund is recorded when it is more
/// than 0, and the events that tell Seat Inventory, Payment and the audit log are written.
/// </summary>
public sealed class CancelBookingHandler
{
    private const string AuditAction = "CancelBooking";
    private const string AuditEventAction = "BookingCancelled";

    // The booking row is locked so a repeat, or two requests at once, see one outcome.
    private const string SelectBookingSql = """
        SELECT b.customer_id, b.status, b.total_amount, b.payment_id, b.trip_id, t.departure_at
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        WHERE b.booking_id = @BookingId
        FOR UPDATE OF b
        """;

    private const string SelectCancellationSql = """
        SELECT c.kind, c.reason, c.refund_percent, c.refund_amount, c.cancelled_at, r.status AS refund_status
        FROM cancellations.cancellations c
        LEFT JOIN cancellations.refunds r ON r.booking_id = c.booking_id
        WHERE c.booking_id = @BookingId
        """;

    private const string CancelBookingSql = """
        UPDATE bookings.bookings SET status = 'Cancelled'
        WHERE booking_id = @BookingId AND status = 'Confirmed'
        """;

    private const string InsertCancellationSql = """
        INSERT INTO cancellations.cancellations (booking_id, kind, reason, refund_percent, refund_amount, cancelled_by, cancelled_at)
        VALUES (@BookingId, 'CustomerCancelled', NULL, @RefundPercent, @RefundAmount, @CancelledBy, @CancelledAt)
        """;

    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at)
        VALUES (@BookingId, @PaymentId, 'Cancellation', @Amount, @RefundKey, 'Requested', @RequestedAt)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IBookingSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<CancelBookingHandler> _logger;

    public CancelBookingHandler(
        IWriterConnectionFactory writerFactory, IBookingSettings settings, IClock clock, ILogger<CancelBookingHandler> logger)
    {
        _writerFactory = writerFactory;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Cancels the booking for its customer, or says why it cannot be; a repeat returns the same cancellation.</summary>
    public async Task<CancelBookingResult> HandleAsync(CancelBookingTarget target, CancellationToken cancellationToken)
    {
        BookingSettingsValues settings = _settings.Current;
        DateTimeOffset now = _clock.UtcNow;
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        BookingRow? booking = await GetBookingAsync(writer, target.BookingId, cancellationToken);
        CancelBookingResult? earlyResult = await DecideEarlyAsync(writer, target, booking, now, cancellationToken);
        if (earlyResult != null)
        {
            return earlyResult;
        }

        DateTimeOffset departureAt = UtcTimes.ToOffset(booking!.DepartureAt);
        int refundPercent = RefundRules.CalculatePercent(departureAt - now, settings.CancellationTiers);
        long refundAmount = RefundRules.CalculateAmount(booking.TotalAmount, refundPercent);
        var refund = new RefundDecision(refundPercent, refundAmount, now);
        await CancelAsync(writer, target, booking, refund, cancellationToken);
        await RecordEventsAsync(writer, target, booking, refund, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "Booking {BookingId} cancelled by its customer with refund {RefundPercent} percent, {RefundAmount} INR",
            target.BookingId, refundPercent, refundAmount);
        return new CancelBookingResult
        {
            Outcome = CancelBookingOutcome.Cancelled,
            Response = ToCancellation(target.BookingId, refund),
        };
    }

    // Whose booking comes first, so a stranger learns nothing about its status or its trip. A booking
    // already cancelled by its customer is answered with that cancellation, even after departure.
    private static async Task<CancelBookingResult?> DecideEarlyAsync(
        IWriterConnection writer, CancelBookingTarget target, BookingRow? booking, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (booking is null)
        {
            return new CancelBookingResult { Outcome = CancelBookingOutcome.NotFound };
        }

        if (booking.CustomerId != target.Caller.AccountId)
        {
            return new CancelBookingResult { Outcome = CancelBookingOutcome.NotTheCustomersBooking };
        }

        BookingStatus status = Enum.Parse<BookingStatus>(booking.Status);
        if (status == BookingStatus.Cancelled)
        {
            return await AnswerForCancelledBookingAsync(writer, target.BookingId, cancellationToken);
        }

        if (status != BookingStatus.Confirmed)
        {
            return new CancelBookingResult { Outcome = CancelBookingOutcome.BookingNotConfirmed, BookingStatus = status };
        }

        DateTimeOffset departureAt = UtcTimes.ToOffset(booking.DepartureAt);
        if (BookingRules.HasDeparted(departureAt, now))
        {
            return new CancelBookingResult { Outcome = CancelBookingOutcome.TripDeparted, DepartedAt = departureAt };
        }

        return null;
    }

    private static async Task<CancelBookingResult> AnswerForCancelledBookingAsync(
        IWriterConnection writer, Guid bookingId, CancellationToken cancellationToken)
    {
        StoredCancellationRow? stored = await writer.Connection.QuerySingleOrDefaultAsync<StoredCancellationRow>(new CommandDefinition(
            SelectCancellationSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
        if (stored is null || stored.Kind != nameof(CancellationKind.CustomerCancelled))
        {
            return new CancelBookingResult
            {
                Outcome = CancelBookingOutcome.BookingNotConfirmed,
                BookingStatus = BookingStatus.Cancelled,
            };
        }

        return new CancelBookingResult
        {
            Outcome = CancelBookingOutcome.AlreadyCancelled,
            Response = ToCancellation(bookingId, stored),
        };
    }

    private static async Task CancelAsync(
        IWriterConnection writer, CancelBookingTarget target, BookingRow booking, RefundDecision refund, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            CancelBookingSql, new { target.BookingId }, writer.Transaction, cancellationToken: cancellationToken));
        var cancellation = new
        {
            target.BookingId,
            RefundPercent = refund.Percent,
            RefundAmount = refund.Amount,
            CancelledBy = target.Caller.AccountId,
            CancelledAt = refund.Now,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertCancellationSql, cancellation, writer.Transaction, cancellationToken: cancellationToken));
        if (refund.Amount > 0)
        {
            var refundRow = new
            {
                target.BookingId,
                booking.PaymentId,
                refund.Amount,
                RefundKey = RefundRules.CancellationKey(target.BookingId),
                RequestedAt = refund.Now,
            };
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                InsertRefundSql, refundRow, writer.Transaction, cancellationToken: cancellationToken));
        }
    }

    // The events describe the booking as just recorded, so they are loaded after the change, in the same transaction.
    private static async Task RecordEventsAsync(
        IWriterConnection writer, CancelBookingTarget target, BookingRow booking, RefundDecision refund, CancellationToken cancellationToken)
    {
        var recorder = new BookingEventRecorder(writer, target.CorrelationId);
        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, target.BookingId, cancellationToken);
        var info = new CancelledEventInfo
        {
            Kind = nameof(CancellationKind.CustomerCancelled),
            RefundPercent = refund.Percent,
            RefundAmount = refund.Amount,
        };
        await recorder.RecordCancelledAsync(context, info, cancellationToken);
        if (refund.Amount > 0)
        {
            await recorder.RecordRefundRequestedAsync(
                target.BookingId, booking.PaymentId!.Value, refund.Amount, RefundReason.Cancellation, cancellationToken);
        }

        var details = new { tripId = booking.TripId, refundPercent = refund.Percent, refundAmount = refund.Amount };
        await recorder.RecordAuditAsync(target.BookingId, target.Caller, AuditEventAction, details, cancellationToken);
    }

    private static Cancellation ToCancellation(Guid bookingId, RefundDecision refund)
    {
        RefundStatus? refundStatus = null;
        if (refund.Amount > 0)
        {
            refundStatus = RefundStatus.Requested;
        }

        return new Cancellation
        {
            BookingId = bookingId,
            Kind = CancellationKind.CustomerCancelled,
            Reason = null,
            RefundPercent = refund.Percent,
            RefundAmount = refund.Amount,
            Currency = BookingEventTypes.Currency,
            CancelledAt = refund.Now,
            RefundStatus = refundStatus,
        };
    }

    private static Cancellation ToCancellation(Guid bookingId, StoredCancellationRow stored)
    {
        RefundStatus? refundStatus = null;
        if (stored.RefundStatus is not null)
        {
            refundStatus = Enum.Parse<RefundStatus>(stored.RefundStatus);
        }

        return new Cancellation
        {
            BookingId = bookingId,
            Kind = Enum.Parse<CancellationKind>(stored.Kind),
            Reason = stored.Reason,
            RefundPercent = stored.RefundPercent,
            RefundAmount = stored.RefundAmount,
            Currency = BookingEventTypes.Currency,
            CancelledAt = UtcTimes.ToOffset(stored.CancelledAt),
            RefundStatus = refundStatus,
        };
    }

    private static async Task<BookingRow?> GetBookingAsync(IWriterConnection writer, Guid bookingId, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition(
            SelectBookingSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>The refund worked out at the moment of cancelling.</summary>
    /// <param name="Percent">The share refunded, 0 to 100.</param>
    /// <param name="Amount">The refund in whole rupees, rounded down.</param>
    /// <param name="Now">The cancellation time.</param>
    private sealed record RefundDecision(int Percent, long Amount, DateTimeOffset Now);

    private sealed class BookingRow
    {
        public string CustomerId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public long TotalAmount { get; init; }

        public Guid? PaymentId { get; init; }

        public Guid TripId { get; init; }

        public DateTime DepartureAt { get; init; }
    }

    private sealed class StoredCancellationRow
    {
        public string Kind { get; init; } = string.Empty;

        public string? Reason { get; init; }

        public int RefundPercent { get; init; }

        public long RefundAmount { get; init; }

        public DateTime CancelledAt { get; init; }

        public string? RefundStatus { get; init; }
    }
}
