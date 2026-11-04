namespace RouteGo.Booking.Api.Features.Cancellations.CreateForceCancellation;

/// <summary>
/// Cancels a confirmed booking for an admin, with the admin's reason and chosen refund share (the whole
/// payment when none is chosen). In one transaction: the booking becomes Cancelled, the cancellation
/// and, when more than 0 comes back, the refund are recorded, and the events that tell Seat Inventory,
/// Payment and the audit log are written. No refund schedule applies; only the force-cancel window.
/// </summary>
public sealed class CreateForceCancellationHandler
{
    private const string AuditAction = "CreateForceCancellation";
    private const string AuditEventAction = "BookingForceCancelled";

    // The booking row is locked so a repeat, or two requests at once, see one outcome.
    private const string SelectBookingSql = """
        SELECT b.status, b.total_amount, b.payment_id, b.trip_id, t.departure_at
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
        VALUES (@BookingId, 'AdminCancelled', @Reason, @RefundPercent, @RefundAmount, @CancelledBy, @CancelledAt)
        """;

    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at)
        VALUES (@BookingId, @PaymentId, 'Cancellation', @Amount, @RefundKey, 'Requested', @RequestedAt)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IBookingSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<CreateForceCancellationHandler> _logger;

    public CreateForceCancellationHandler(
        IWriterConnectionFactory writerFactory, IBookingSettings settings, IClock clock, ILogger<CreateForceCancellationHandler> logger)
    {
        _writerFactory = writerFactory;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Cancels the booking for the admin, or says why it cannot be; a repeat returns the existing cancellation.</summary>
    public async Task<CreateForceCancellationResult> HandleAsync(
        ForceCancellation request, Caller admin, string correlationId, CancellationToken cancellationToken)
    {
        BookingSettingsValues settings = _settings.Current;
        DateTimeOffset now = _clock.UtcNow;
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        BookingRow? booking = await writer.Connection.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition(
            SelectBookingSql, new { request.BookingId }, writer.Transaction, cancellationToken: cancellationToken));
        CreateForceCancellationResult? earlyResult = await DecideEarlyAsync(writer, request.BookingId, booking, settings, now, cancellationToken);
        if (earlyResult != null)
        {
            return earlyResult;
        }

        int refundPercent = request.RefundPercent ?? RefundRules.DefaultForceCancelPercent();
        long refundAmount = RefundRules.CalculateAmount(booking!.TotalAmount, refundPercent);
        var refund = new RefundDecision(refundPercent, refundAmount, now);
        var actor = new ForceCancelActor(admin, correlationId);
        await CancelAsync(writer, request, booking, refund, actor, cancellationToken);
        await RecordEventsAsync(writer, request, booking, refund, actor, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "Booking {BookingId} force-cancelled by an admin with refund {RefundPercent} percent, {RefundAmount} INR",
            request.BookingId, refundPercent, refundAmount);
        return new CreateForceCancellationResult
        {
            Outcome = CreateForceCancellationOutcome.Cancelled,
            Response = ToCancellation(request, refund),
        };
    }

    // A booking already cancelled by an admin is answered with that cancellation whatever the time;
    // the window is only a limit on cancelling now. Exactly at the end of the window is still allowed.
    private static async Task<CreateForceCancellationResult?> DecideEarlyAsync(
        IWriterConnection writer, Guid bookingId, BookingRow? booking, BookingSettingsValues settings, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (booking is null)
        {
            return new CreateForceCancellationResult { Outcome = CreateForceCancellationOutcome.NotFound };
        }

        BookingStatus status = Enum.Parse<BookingStatus>(booking.Status);
        if (status == BookingStatus.Cancelled)
        {
            return await AnswerForCancelledBookingAsync(writer, bookingId, cancellationToken);
        }

        if (status != BookingStatus.Confirmed)
        {
            return new CreateForceCancellationResult
            {
                Outcome = CreateForceCancellationOutcome.BookingNotConfirmed,
                BookingStatus = status,
            };
        }

        DateTimeOffset departureAt = UtcTimes.ToOffset(booking.DepartureAt);
        if (now > departureAt.AddDays(settings.ForceCancelWindowDays))
        {
            return new CreateForceCancellationResult
            {
                Outcome = CreateForceCancellationOutcome.ForceCancelWindowClosed,
                DepartedAt = departureAt,
                WindowDays = settings.ForceCancelWindowDays,
            };
        }

        return null;
    }

    private static async Task<CreateForceCancellationResult> AnswerForCancelledBookingAsync(
        IWriterConnection writer, Guid bookingId, CancellationToken cancellationToken)
    {
        StoredCancellationRow? stored = await writer.Connection.QuerySingleOrDefaultAsync<StoredCancellationRow>(new CommandDefinition(
            SelectCancellationSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
        if (stored is null || stored.Kind != nameof(CancellationKind.AdminCancelled))
        {
            return new CreateForceCancellationResult
            {
                Outcome = CreateForceCancellationOutcome.BookingNotConfirmed,
                BookingStatus = BookingStatus.Cancelled,
            };
        }

        return new CreateForceCancellationResult
        {
            Outcome = CreateForceCancellationOutcome.AlreadyCancelled,
            Response = ToCancellation(bookingId, stored),
        };
    }

    private static async Task CancelAsync(
        IWriterConnection writer, ForceCancellation request, BookingRow booking, RefundDecision refund, ForceCancelActor actor,
        CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            CancelBookingSql, new { request.BookingId }, writer.Transaction, cancellationToken: cancellationToken));
        var cancellation = new
        {
            request.BookingId,
            request.Reason,
            RefundPercent = refund.Percent,
            RefundAmount = refund.Amount,
            CancelledBy = actor.Admin.AccountId,
            CancelledAt = refund.Now,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertCancellationSql, cancellation, writer.Transaction, cancellationToken: cancellationToken));
        if (refund.Amount > 0)
        {
            var refundRow = new
            {
                request.BookingId,
                booking.PaymentId,
                refund.Amount,
                RefundKey = RefundRules.CancellationKey(request.BookingId),
                RequestedAt = refund.Now,
            };
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                InsertRefundSql, refundRow, writer.Transaction, cancellationToken: cancellationToken));
        }
    }

    // The events describe the booking as just recorded, so they are loaded after the change, in the same transaction.
    private static async Task RecordEventsAsync(
        IWriterConnection writer, ForceCancellation request, BookingRow booking, RefundDecision refund, ForceCancelActor actor,
        CancellationToken cancellationToken)
    {
        var recorder = new BookingEventRecorder(writer, actor.CorrelationId);
        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, request.BookingId, cancellationToken);
        var info = new CancelledEventInfo
        {
            Kind = nameof(CancellationKind.AdminCancelled),
            Reason = request.Reason,
            RefundPercent = refund.Percent,
            RefundAmount = refund.Amount,
        };
        await recorder.RecordCancelledAsync(context, info, cancellationToken);
        if (refund.Amount > 0)
        {
            await recorder.RecordRefundRequestedAsync(
                request.BookingId, booking.PaymentId!.Value, refund.Amount, RefundReason.Cancellation, cancellationToken);
        }

        var details = new
        {
            tripId = booking.TripId,
            refundPercent = refund.Percent,
            refundAmount = refund.Amount,
            reason = request.Reason,
        };
        await recorder.RecordAuditAsync(request.BookingId, actor.Admin, AuditEventAction, details, cancellationToken);
    }

    private static Cancellation ToCancellation(ForceCancellation request, RefundDecision refund)
    {
        RefundStatus? refundStatus = null;
        if (refund.Amount > 0)
        {
            refundStatus = RefundStatus.Requested;
        }

        return new Cancellation
        {
            BookingId = request.BookingId,
            Kind = CancellationKind.AdminCancelled,
            Reason = request.Reason,
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

    /// <summary>The refund worked out at the moment of cancelling.</summary>
    /// <param name="Percent">The share refunded, 0 to 100.</param>
    /// <param name="Amount">The refund in whole rupees, rounded down.</param>
    /// <param name="Now">The cancellation time.</param>
    private sealed record RefundDecision(int Percent, long Amount, DateTimeOffset Now);

    /// <summary>Who cancelled, and the correlation ID the events are recorded under.</summary>
    /// <param name="Admin">The calling admin.</param>
    /// <param name="CorrelationId">The request's correlation ID.</param>
    private sealed record ForceCancelActor(Caller Admin, string CorrelationId);

    private sealed class BookingRow
    {
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
