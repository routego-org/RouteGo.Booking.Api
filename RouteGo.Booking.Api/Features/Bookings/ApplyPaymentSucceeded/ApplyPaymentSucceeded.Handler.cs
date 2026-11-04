namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentSucceeded;

/// <summary>
/// Finishes a booking whose payment succeeded: confirms the seats with Seat Inventory and the booking
/// with them, or, when the booking can no longer use the money, requests a full refund. No transaction
/// is held open across the call to Seat Inventory; every status move afterwards is conditional on the
/// booking still waiting, so a payment racing an expiry moves the booking once and the loser's payment
/// ends in a refund.
/// </summary>
public sealed partial class ApplyPaymentSucceededHandler : IEventWorker
{
    private const string AuditAction = "ApplyPaymentSucceeded";
    private const string PaymentSucceededType = "PaymentSucceeded";

    private static readonly string[] _waitingStatuses =
    {
        nameof(BookingStatus.SeatsHeld),
        nameof(BookingStatus.PaymentPending),
    };

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ISeatInventoryClient _seatInventory;
    private readonly IClock _clock;
    private readonly ILogger<ApplyPaymentSucceededHandler> _logger;

    public ApplyPaymentSucceededHandler(
        IWriterConnectionFactory writerFactory, ISeatInventoryClient seatInventory, IClock clock, ILogger<ApplyPaymentSucceededHandler> logger)
    {
        _writerFactory = writerFactory;
        _seatInventory = seatInventory;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EventType
    {
        get { return PaymentSucceededType; }
    }

    /// <summary>
    /// Applies one PaymentSucceeded event. Applied covers a confirmed booking, a failed one with its
    /// refund, a late payment refunded and every repeat; Postponed is a confirm Seat Inventory could not
    /// answer yet; Rejected is an unknown booking, a different amount or unusable data.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        PaidBooking? payment = Read(receivedEvent);
        if (payment is null)
        {
            return EventOutcome.Rejected(
                $"PaymentSucceeded event {receivedEvent.EventId} needs a bookingId, a paymentId and a whole-rupee amount of 1 or more");
        }

        BookingRow? booking = await GetBookingAsync(payment.BookingId, cancellationToken);
        EventOutcome? refusal = Check(booking, payment);
        if (refusal != null)
        {
            return refusal;
        }

        BookingStatus status = Enum.Parse<BookingStatus>(booking!.Status);
        if (BookingRules.IsAwaitingPaymentResult(status))
        {
            return await ConfirmAsync(payment, booking.TripId, cancellationToken);
        }

        if (status == BookingStatus.Expired || status == BookingStatus.Failed)
        {
            await RefundLatePaymentAsync(payment, cancellationToken);
            return EventOutcome.Applied();
        }

        return Nothing(status, payment);
    }

    // Initiated has no payment to report yet, so the event waits for the booking to move on; Confirmed
    // and Cancelled mean this success was already applied.
    private static EventOutcome Nothing(BookingStatus status, PaidBooking payment)
    {
        if (status != BookingStatus.Initiated)
        {
            return EventOutcome.Applied();
        }

        return EventOutcome.Postponed(
            "bookingNotWaiting", $"booking {payment.BookingId} has not reached the point of waiting for its payment");
    }

    private async Task<EventOutcome> ConfirmAsync(PaidBooking payment, Guid tripId, CancellationToken cancellationToken)
    {
        ConfirmSeatsResult confirmed = await _seatInventory.ConfirmSeatsAsync(tripId, payment.BookingId, payment.CorrelationId, cancellationToken);
        if (confirmed.Outcome == ConfirmSeatsOutcome.Unavailable || confirmed.Outcome == ConfirmSeatsOutcome.TimedOut)
        {
            _logger.LogWarning(
                "Seat Inventory did not confirm the seats of booking {BookingId} ({Outcome}); the payment result is retried (correlation {CorrelationId})",
                payment.BookingId, confirmed.Outcome, payment.CorrelationId);
            return EventOutcome.Postponed(
                "seatInventoryUnavailable", $"Seat Inventory could not confirm the seats of booking {payment.BookingId}: {confirmed.Outcome}");
        }

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool isMoved = await MoveAsync(writer, payment, confirmed.Outcome, cancellationToken);
        if (!isMoved)
        {
            await RefundIfLateAsync(writer, payment, cancellationToken);
        }

        await writer.CommitAsync(cancellationToken);
        return EventOutcome.Applied();
    }

    private async Task<bool> MoveAsync(
        IWriterConnection writer, PaidBooking payment, ConfirmSeatsOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome == ConfirmSeatsOutcome.Confirmed)
        {
            return await ConfirmBookingAsync(writer, payment, cancellationToken);
        }

        FailureReason reason = FailureReason.SeatsLost;
        if (outcome == ConfirmSeatsOutcome.TripCancelled)
        {
            reason = FailureReason.TripCancelled;
        }

        return await FailBookingAsync(writer, payment, reason, cancellationToken);
    }

    private async Task<bool> ConfirmBookingAsync(IWriterConnection writer, PaidBooking payment, CancellationToken cancellationToken)
    {
        var confirmation = new
        {
            payment.BookingId,
            payment.PaymentId,
            ConfirmedAt = _clock.UtcNow,
            WaitingStatuses = _waitingStatuses,
        };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            ConfirmSql, confirmation, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return false;
        }

        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, payment.BookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, payment.CorrelationId);
        await recorder.RecordConfirmedAsync(context, cancellationToken);
        _logger.LogInformation(
            "Booking {BookingId} confirmed by payment {PaymentId} (correlation {CorrelationId})",
            payment.BookingId, payment.PaymentId, payment.CorrelationId);
        return true;
    }

    private async Task<bool> FailBookingAsync(
        IWriterConnection writer, PaidBooking payment, FailureReason reason, CancellationToken cancellationToken)
    {
        var failure = new { payment.BookingId, FailureReason = reason.ToString(), WaitingStatuses = _waitingStatuses };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            FailSql, failure, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return false;
        }

        RefundReason refundReason = RefundReason.SeatsLost;
        if (reason == FailureReason.TripCancelled)
        {
            refundReason = RefundReason.TripCancelled;
        }

        await InsertRefundAsync(writer, payment, refundReason, cancellationToken);
        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, payment.BookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, payment.CorrelationId);
        await recorder.RecordFailedAsync(context, reason, cancellationToken);
        await recorder.RecordRefundRequestedAsync(payment.BookingId, payment.PaymentId, payment.Amount, refundReason, cancellationToken);
        _logger.LogInformation(
            "Booking {BookingId} failed with {FailureReason} after payment {PaymentId}; a full refund is requested (correlation {CorrelationId})",
            payment.BookingId, reason, payment.PaymentId, payment.CorrelationId);
        return true;
    }

    private async Task RefundLatePaymentAsync(PaidBooking payment, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        await RefundIfLateAsync(writer, payment, cancellationToken);
        await writer.CommitAsync(cancellationToken);
    }

    // The status is read under the booking's lock, so the decision to refund cannot be overtaken. The
    // booking itself is never changed: a late payment leaves it Expired or Failed.
    private async Task RefundIfLateAsync(IWriterConnection writer, PaidBooking payment, CancellationToken cancellationToken)
    {
        string? status = await writer.Connection.ExecuteScalarAsync<string>(new CommandDefinition(
            SelectStatusSql, new { payment.BookingId }, writer.Transaction, cancellationToken: cancellationToken));
        bool isLate = status == nameof(BookingStatus.Expired) || status == nameof(BookingStatus.Failed);
        if (!isLate)
        {
            return;
        }

        bool isInserted = await InsertRefundAsync(writer, payment, RefundReason.LatePayment, cancellationToken);
        if (!isInserted)
        {
            return;
        }

        var recorder = new BookingEventRecorder(writer, payment.CorrelationId);
        await recorder.RecordRefundRequestedAsync(payment.BookingId, payment.PaymentId, payment.Amount, RefundReason.LatePayment, cancellationToken);
        _logger.LogInformation(
            "Late payment {PaymentId} of booking {BookingId} is refunded in full (correlation {CorrelationId})",
            payment.PaymentId, payment.BookingId, payment.CorrelationId);
    }

    private async Task<bool> InsertRefundAsync(
        IWriterConnection writer, PaidBooking payment, RefundReason reason, CancellationToken cancellationToken)
    {
        var refund = new
        {
            payment.BookingId,
            payment.PaymentId,
            Reason = reason.ToString(),
            payment.Amount,
            RefundKey = RefundRules.PaymentKey(payment.BookingId),
            RequestedAt = _clock.UtcNow,
        };
        int inserted = await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertRefundSql, refund, writer.Transaction, cancellationToken: cancellationToken));
        return inserted == 1;
    }

    private async Task<BookingRow?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        return await writer.Connection.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition(
            SelectBookingSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static EventOutcome? Check(BookingRow? booking, PaidBooking payment)
    {
        if (booking is null)
        {
            return EventOutcome.Rejected(
                "bookingUnknown", $"event for payment {payment.PaymentId}: booking {payment.BookingId} does not exist");
        }

        if (booking.TotalAmount != payment.Amount)
        {
            return EventOutcome.Rejected(
                "amountMismatch",
                $"payment {payment.PaymentId} is {payment.Amount} but booking {payment.BookingId} totals {booking.TotalAmount}");
        }

        return null;
    }

    private static PaidBooking? Read(ReceivedEvent receivedEvent)
    {
        PaymentSucceededData? data = TryRead(receivedEvent.Data);
        bool hasBooking = Guid.TryParse(data?.BookingId, out Guid bookingId);
        bool hasPayment = Guid.TryParse(data?.PaymentId, out Guid paymentId);
        long? amount = WholeAmount(data?.Amount);
        if (!hasBooking || !hasPayment || amount is null)
        {
            return null;
        }

        return new PaidBooking
        {
            BookingId = bookingId,
            PaymentId = paymentId,
            Amount = amount.Value,
            CorrelationId = receivedEvent.CorrelationId,
        };
    }

    // Data that cannot be read at all is unusable like any other; the handler names it.
    private static PaymentSucceededData? TryRead(JsonElement data)
    {
        try
        {
            return data.Deserialize<PaymentSucceededData>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? WholeAmount(decimal? amount)
    {
        if (amount is null)
        {
            return null;
        }

        bool isWhole = amount.Value == decimal.Truncate(amount.Value);
        bool isInRange = amount.Value >= 1 && amount.Value <= long.MaxValue;
        if (!isWhole || !isInRange)
        {
            return null;
        }

        return (long)amount.Value;
    }

    private sealed record PaidBooking
    {
        public required Guid BookingId { get; init; }

        public required Guid PaymentId { get; init; }

        public required long Amount { get; init; }

        public required string CorrelationId { get; init; }
    }

    private sealed class BookingRow
    {
        public Guid TripId { get; init; }

        public string Status { get; init; } = string.Empty;

        public long TotalAmount { get; init; }
    }
}
