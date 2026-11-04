namespace RouteGo.Booking.Api.Features.Cancellations.CancelTripBookings;

/// <summary>
/// Applies Fleet's TripCancelled: the trip's snapshot is marked cancelled first (so nothing new can be
/// booked on it), then every confirmed booking is cancelled with a full refund and every booking still
/// waiting for payment is failed. Each booking is ended in its own transaction, decided on its status
/// at that moment, so a retry after a partial run finishes the rest and never repeats a booking. Seat
/// Inventory frees the seats when it receives the events recorded here; it is not called.
/// </summary>
public sealed class CancelTripBookingsHandler : IEventWorker
{
    private const string AuditAction = "CancelTripBookings";
    private const string TripCancelledType = "TripCancelled";

    // An operator cancellation always returns the whole total.
    private const int FullRefundPercent = 100;

    // The snapshot row is written only while it is not yet cancelled, so the first reason and time stay.
    private const string MarkCancelledSql = """
        INSERT INTO trip_snapshots.trip_snapshots AS t (trip_id, is_cancelled, cancellation_reason, cancelled_at)
        VALUES (@TripId, true, @Reason, @Now)
        ON CONFLICT (trip_id) DO UPDATE SET
            is_cancelled = true,
            cancellation_reason = EXCLUDED.cancellation_reason,
            cancelled_at = EXCLUDED.cancelled_at
        WHERE t.is_cancelled = false
        """;

    // Initiated bookings are left out: their creation is finishing and Seat Inventory refuses it on a cancelled trip.
    private const string ListBookingsSql = """
        SELECT booking_id
        FROM bookings.bookings
        WHERE trip_id = @TripId AND status = ANY(@Statuses)
        ORDER BY booking_id
        """;

    private const string LockBookingSql = """
        SELECT status, total_amount, payment_id
        FROM bookings.bookings
        WHERE booking_id = @BookingId
        FOR UPDATE
        """;

    private const string MoveBookingSql = """
        UPDATE bookings.bookings
        SET status = @To, failure_reason = @FailureReason
        WHERE booking_id = @BookingId AND status = @From
        """;

    private const string InsertCancellationSql = """
        INSERT INTO cancellations.cancellations (booking_id, kind, reason, refund_percent, refund_amount, cancelled_at)
        VALUES (@BookingId, @Kind, @Reason, @RefundPercent, @RefundAmount, @Now)
        """;

    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at)
        VALUES (@BookingId, @PaymentId, @Reason, @Amount, @RefundKey, @Status, @Now)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly ILogger<CancelTripBookingsHandler> _logger;

    public CancelTripBookingsHandler(IWriterConnectionFactory writerFactory, IClock clock, ILogger<CancelTripBookingsHandler> logger)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _logger = logger;
    }

    private enum Ending
    {
        Untouched,
        Cancelled,
        Failed,
    }

    /// <inheritdoc />
    public string EventType
    {
        get
        {
            return TripCancelledType;
        }
    }

    /// <summary>
    /// Applies one TripCancelled. Applied covers a cancellation done now, a repeat, and a retry that
    /// finished what was left; Rejected is data without a usable trip or reason. A failure while ending
    /// a booking is thrown, so the queue delivers the event again.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        TripCancelledMessage? message = TripCancelledReader.Read(receivedEvent.Data, out string? problem);
        if (message is null)
        {
            return EventOutcome.Rejected($"TripCancelled event {receivedEvent.EventId} cannot cancel a trip: {problem}");
        }

        IReadOnlyList<Guid> bookingIds = await MarkCancelledAsync(message, cancellationToken);
        var endings = new List<Ending>();
        foreach (Guid bookingId in bookingIds)
        {
            Ending ending = await EndBookingAsync(bookingId, message, receivedEvent.CorrelationId, cancellationToken);
            endings.Add(ending);
        }

        int cancelled = endings.Count(ending => ending == Ending.Cancelled);
        int failed = endings.Count(ending => ending == Ending.Failed);
        _logger.LogInformation(
            "Trip {TripId} cancellation applied: {CancelledCount} bookings cancelled, {FailedCount} failed (correlation {CorrelationId})",
            message.TripId, cancelled, failed, receivedEvent.CorrelationId);
        return EventOutcome.Applied();
    }

    private async Task<IReadOnlyList<Guid>> MarkCancelledAsync(TripCancelledMessage message, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var mark = new { message.TripId, message.Reason, Now = _clock.UtcNow };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            MarkCancelledSql, mark, writer.Transaction, cancellationToken: cancellationToken));
        string[] statuses =
        {
            nameof(BookingStatus.Confirmed), nameof(BookingStatus.SeatsHeld), nameof(BookingStatus.PaymentPending),
        };
        IEnumerable<Guid> bookingIds = await writer.Connection.QueryAsync<Guid>(new CommandDefinition(
            ListBookingsSql, new { message.TripId, Statuses = statuses }, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
        return bookingIds.ToList();
    }

    // The booking is locked and its status read again, so a booking that moved since the list is judged as it is now.
    private async Task<Ending> EndBookingAsync(
        Guid bookingId, TripCancelledMessage message, string correlationId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        BookingRow booking = await writer.Connection.QuerySingleAsync<BookingRow>(new CommandDefinition(
            LockBookingSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
        BookingStatus status = Enum.Parse<BookingStatus>(booking.Status);
        Ending ending = Ending.Untouched;
        if (status == BookingStatus.Confirmed)
        {
            await CancelConfirmedAsync(writer, bookingId, booking, message, correlationId, cancellationToken);
            ending = Ending.Cancelled;
        }
        else if (BookingRules.IsAwaitingPaymentResult(status))
        {
            await FailUnpaidAsync(writer, bookingId, status, correlationId, cancellationToken);
            ending = Ending.Failed;
        }

        if (ending != Ending.Untouched)
        {
            await writer.CommitAsync(cancellationToken);
        }

        return ending;
    }

    private async Task CancelConfirmedAsync(
        IWriterConnection writer, Guid bookingId, BookingRow booking, TripCancelledMessage message, string correlationId,
        CancellationToken cancellationToken)
    {
        await MoveAsync(writer, bookingId, BookingStatus.Confirmed, BookingStatus.Cancelled, null, cancellationToken);
        long amount = RefundRules.CalculateAmount(booking.TotalAmount, FullRefundPercent);
        Guid paymentId = booking.PaymentId!.Value;
        var cancellation = new
        {
            BookingId = bookingId,
            Kind = nameof(CancellationKind.OperatorCancelled),
            message.Reason,
            RefundPercent = FullRefundPercent,
            RefundAmount = amount,
            Now = _clock.UtcNow,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertCancellationSql, cancellation, writer.Transaction, cancellationToken: cancellationToken));
        var refund = new
        {
            BookingId = bookingId,
            PaymentId = paymentId,
            Reason = nameof(RefundReason.Cancellation),
            Amount = amount,
            RefundKey = RefundRules.CancellationKey(bookingId),
            Status = nameof(RefundStatus.Requested),
            Now = _clock.UtcNow,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertRefundSql, refund, writer.Transaction, cancellationToken: cancellationToken));
        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, bookingId, cancellationToken);
        var info = new CancelledEventInfo
        {
            Kind = nameof(CancellationKind.OperatorCancelled),
            Reason = message.Reason,
            RefundPercent = FullRefundPercent,
            RefundAmount = amount,
        };
        var recorder = new BookingEventRecorder(writer, correlationId);
        await recorder.RecordCancelledAsync(context, info, cancellationToken);
        await recorder.RecordRefundRequestedAsync(bookingId, paymentId, amount, RefundReason.Cancellation, cancellationToken);
    }

    private static async Task FailUnpaidAsync(
        IWriterConnection writer, Guid bookingId, BookingStatus from, string correlationId, CancellationToken cancellationToken)
    {
        await MoveAsync(writer, bookingId, from, BookingStatus.Failed, nameof(FailureReason.TripCancelled), cancellationToken);
        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, bookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, correlationId);
        await recorder.RecordFailedAsync(context, FailureReason.TripCancelled, cancellationToken);
    }

    // The booking is locked and was just read in this status, so the move must hit exactly one row.
    private static async Task MoveAsync(
        IWriterConnection writer, Guid bookingId, BookingStatus from, BookingStatus to, string? failureReason,
        CancellationToken cancellationToken)
    {
        var move = new { BookingId = bookingId, From = from.ToString(), To = to.ToString(), FailureReason = failureReason };
        int rows = await writer.Connection.ExecuteAsync(new CommandDefinition(
            MoveBookingSql, move, writer.Transaction, cancellationToken: cancellationToken));
        if (rows != 1)
        {
            throw new InvalidOperationException($"Booking {bookingId} could not move from {from} to {to}: it was not in {from}.");
        }
    }

    private sealed class BookingRow
    {
        public string Status { get; init; } = string.Empty;

        public long TotalAmount { get; init; }

        public Guid? PaymentId { get; init; }
    }
}
