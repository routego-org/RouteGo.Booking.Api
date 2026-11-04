namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

/// <summary>
/// Creates a booking in four steps, each its own short transaction so no database transaction is ever
/// open while Seat Inventory or Payment is being called: record the booking after checking the trip
/// and price, hold the seats, start the payment, and finish. Every move of the booking's status is
/// conditional on the status the step expects, and a booking that ends failed records its
/// BookingFailed event in the same transaction as the move.
/// </summary>
public sealed partial class CreateBookingHandler
{
    private const string AuditAction = "CreateBooking";

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ISeatInventoryClient _seatInventory;
    private readonly IPaymentClient _payment;
    private readonly IClock _clock;

    public CreateBookingHandler(
        IWriterConnectionFactory writerFactory, ISeatInventoryClient seatInventory, IPaymentClient payment, IClock clock)
    {
        _writerFactory = writerFactory;
        _seatInventory = seatInventory;
        _payment = payment;
        _clock = clock;
    }

    /// <summary>Books the seats for the caller, or says why not.</summary>
    /// <param name="order">The caller and the validated request.</param>
    /// <param name="settings">The settings in force, read once by the caller for this request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<CreateBookingResult> HandleAsync(
        BookingOrder order, BookingSettingsValues settings, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        NewBooking booking = ToNewBooking(order);
        CreateBookingResult? refusal = await RecordAsync(booking, settings, now, cancellationToken);
        if (refusal != null)
        {
            return refusal;
        }

        HoldSeatsResult hold = await _seatInventory.HoldSeatsAsync(
            booking.TripId, booking.BookingId, booking.SeatNos, settings.HoldSeconds, booking.CorrelationId, cancellationToken);
        CreateBookingResult? holdRefusal = await ApplyHoldAsync(booking, hold, cancellationToken);
        if (holdRefusal != null)
        {
            return holdRefusal;
        }

        return await StartPaymentAsync(booking, now, cancellationToken);
    }

    private async Task<CreateBookingResult?> ApplyHoldAsync(NewBooking booking, HoldSeatsResult hold, CancellationToken cancellationToken)
    {
        switch (hold.Outcome)
        {
            case HoldSeatsOutcome.Held:
                return await MarkHeldAsync(booking, hold.HoldExpiresAt!.Value, cancellationToken);
            case HoldSeatsOutcome.SeatsUnavailable:
                var unavailable = new FailedStep(FailureReason.SeatsUnavailable, false, SeatsUnavailable(hold.NamedSeats));
                return await EndFailedAsync(booking, BookingStatus.Initiated, unavailable, cancellationToken);
            case HoldSeatsOutcome.TripCancelled:
                var cancelled = new FailedStep(
                    FailureReason.TripCancelled, false, CreateBookingResult.Refused(CreateBookingOutcome.TripCancelled));
                return await EndFailedAsync(booking, BookingStatus.Initiated, cancelled, cancellationToken);
            case HoldSeatsOutcome.TimedOut:
                var timedOut = new FailedStep(
                    FailureReason.InventoryUnavailable, true, CreateBookingResult.Refused(CreateBookingOutcome.UpstreamTimeout));
                return await EndFailedAsync(booking, BookingStatus.Initiated, timedOut, cancellationToken);
            case HoldSeatsOutcome.Unavailable:
                return await EndFailedAsync(booking, BookingStatus.Initiated, InventoryUnusable(true), cancellationToken);
            default:
                // Unknown seats and a refusal for a reason that is not the customer's to fix: the snapshot
                // and Seat Inventory disagree, which retrying will not mend.
                return await EndFailedAsync(booking, BookingStatus.Initiated, InventoryUnusable(false), cancellationToken);
        }
    }

    private async Task<CreateBookingResult> StartPaymentAsync(NewBooking booking, DateTimeOffset now, CancellationToken cancellationToken)
    {
        StartPaymentResult payment = await _payment.StartPaymentAsync(
            booking.BookingId, booking.ExpectedTotal, booking.ContactEmail, booking.CorrelationId, cancellationToken);
        if (payment.Outcome == StartPaymentOutcome.Started)
        {
            return await MarkPaymentPendingAsync(booking, payment, now, cancellationToken);
        }

        var refusal = new CreateBookingResult { Outcome = CreateBookingOutcome.UpstreamUnavailable, IsRetryable = true };
        if (payment.Outcome == StartPaymentOutcome.TimedOut)
        {
            refusal = CreateBookingResult.Refused(CreateBookingOutcome.UpstreamTimeout);
        }

        var step = new FailedStep(FailureReason.PaymentUnavailable, true, refusal);
        return await EndFailedAsync(booking, BookingStatus.SeatsHeld, step, cancellationToken);
    }

    private async Task<CreateBookingResult?> MarkHeldAsync(NewBooking booking, DateTimeOffset holdExpiresAt, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var parameters = new { booking.BookingId, HoldExpiresAt = holdExpiresAt.ToUniversalTime() };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            MarkHeldSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return CreateBookingResult.Refused(CreateBookingOutcome.StateChanged);
        }

        await writer.CommitAsync(cancellationToken);
        return null;
    }

    private async Task<CreateBookingResult> MarkPaymentPendingAsync(
        NewBooking booking, StartPaymentResult payment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var parameters = new { booking.BookingId, payment.PaymentId, payment.CheckoutUrl };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            MarkPaymentPendingSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return CreateBookingResult.Refused(CreateBookingOutcome.StateChanged);
        }

        // Read inside the transaction that wrote it, so the answer is the booking as just recorded.
        Booking response = await GetBookingAsync(writer, booking.BookingId, now, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CreateBookingResult { Outcome = CreateBookingOutcome.Created, Response = response };
    }

    // The failure is recorded first and the release asked for afterwards: if the release never arrives,
    // the BookingFailed event recorded with the move makes Seat Inventory free the seats anyway.
    private async Task<CreateBookingResult> EndFailedAsync(
        NewBooking booking, BookingStatus from, FailedStep step, CancellationToken cancellationToken)
    {
        bool isMoved = await MarkFailedAsync(booking, from, step.Reason, cancellationToken);
        if (isMoved && step.IsReleaseNeeded)
        {
            await _seatInventory.ReleaseSeatsAsync(booking.TripId, booking.BookingId, booking.CorrelationId, cancellationToken);
        }

        if (!isMoved)
        {
            return CreateBookingResult.Refused(CreateBookingOutcome.StateChanged);
        }

        return step.Refusal;
    }

    private async Task<bool> MarkFailedAsync(NewBooking booking, BookingStatus from, FailureReason reason, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var parameters = new { booking.BookingId, From = from.ToString(), Reason = reason.ToString() };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            MarkFailedSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return false;
        }

        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, booking.BookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, booking.CorrelationId);
        await recorder.RecordFailedAsync(context, reason, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return true;
    }

    private static CreateBookingResult SeatsUnavailable(IReadOnlyList<string> seatNos)
    {
        return new CreateBookingResult { Outcome = CreateBookingOutcome.SeatsUnavailable, SeatNos = seatNos };
    }

    private static FailedStep InventoryUnusable(bool isRetryable)
    {
        var refusal = new CreateBookingResult { Outcome = CreateBookingOutcome.UpstreamUnavailable, IsRetryable = isRetryable };
        return new FailedStep(FailureReason.InventoryUnavailable, true, refusal);
    }

    private static NewBooking ToNewBooking(BookingOrder order)
    {
        var seats = new List<NewSeat>();
        foreach (BookingSeatRequest seat in order.Request.Seats!)
        {
            seats.Add(new NewSeat
            {
                SeatNo = seat.SeatNo!.Trim(),
                PassengerName = seat.PassengerName!.Trim(),
                PassengerAge = (int)seat.PassengerAge!.Value,
                PassengerGender = BookingRules.ParseGender(seat.PassengerGender!).ToString(),
            });
        }

        return new NewBooking
        {
            BookingId = Guid.NewGuid(),
            CustomerId = order.Caller.AccountId,
            ContactName = order.Request.ContactName!.Trim(),
            ContactEmail = order.Caller.Email,
            TripId = order.Request.TripId!.Value,
            ExpectedTotal = order.Request.ExpectedTotal!.Value,
            Seats = seats,
            CorrelationId = order.CorrelationId,
        };
    }

    /// <summary>How a hold or payment failure ends the booking and what the customer is told.</summary>
    /// <param name="Reason">The failure reason recorded.</param>
    /// <param name="IsReleaseNeeded">Whether Seat Inventory is asked to release the booking's seats.</param>
    /// <param name="Refusal">The answer to the customer.</param>
    private sealed record FailedStep(FailureReason Reason, bool IsReleaseNeeded, CreateBookingResult Refusal);

    private sealed class NewSeat
    {
        public required string SeatNo { get; init; }

        public required string PassengerName { get; init; }

        public required int PassengerAge { get; init; }

        public required string PassengerGender { get; init; }
    }

    private sealed class NewBooking
    {
        public required Guid BookingId { get; init; }

        public required string CustomerId { get; init; }

        public required string ContactName { get; init; }

        public required string ContactEmail { get; init; }

        public required Guid TripId { get; init; }

        public required long ExpectedTotal { get; init; }

        public required IReadOnlyList<NewSeat> Seats { get; init; }

        public required string CorrelationId { get; init; }

        public IReadOnlyList<string> SeatNos
        {
            get
            {
                return Seats.Select(seat => seat.SeatNo).ToList();
            }
        }
    }
}
