namespace RouteGo.Booking.Api.Features.Bookings.ReleaseBooking;

/// <summary>
/// Abandons an unpaid booking: ends it as failed with reason abandoned and records BookingFailed in
/// one transaction, then asks Seat Inventory to free the seats. The status move is conditional, so an
/// abandon racing a payment result moves the booking once. The seat release comes after the commit and
/// its outcome never undoes the abandon: the recorded event frees the seats if the call is lost.
/// </summary>
public sealed partial class ReleaseBookingHandler
{
    private const string AuditAction = "ReleaseBooking";

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ISeatInventoryClient _seatInventory;
    private readonly IClock _clock;

    public ReleaseBookingHandler(IWriterConnectionFactory writerFactory, ISeatInventoryClient seatInventory, IClock clock)
    {
        _writerFactory = writerFactory;
        _seatInventory = seatInventory;
        _clock = clock;
    }

    /// <summary>Abandons the booking if it is the caller's and still unpaid, or says why not.</summary>
    /// <param name="target">The booking and the caller.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ReleaseBookingResult> HandleAsync(ReleaseTarget target, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        Abandonment abandonment = await AbandonAsync(target, now, cancellationToken);
        if (abandonment.TripIdToRelease is not null)
        {
            await _seatInventory.ReleaseSeatsAsync(
                abandonment.TripIdToRelease.Value, target.BookingId, target.CorrelationId, cancellationToken);
        }

        return abandonment.Result;
    }

    private async Task<Abandonment> AbandonAsync(ReleaseTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        HeadRow? head = await GetHeadAsync(writer, target.BookingId, cancellationToken);
        ReleaseBookingResult? refusal = CheckOwner(head, target.Caller);
        if (refusal != null)
        {
            return new Abandonment(refusal, null);
        }

        ReleaseBookingResult? settled = await SettleWithoutMoveAsync(writer, head!, now, cancellationToken);
        if (settled != null)
        {
            return new Abandonment(settled, null);
        }

        bool isMoved = await MoveToAbandonedAsync(writer, target, cancellationToken);
        if (!isMoved)
        {
            return await LostRaceAsync(writer, target, now, cancellationToken);
        }

        ReleaseBookingResult released = await ToReleasedAsync(writer, target.BookingId, now, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new Abandonment(released, head!.TripId);
    }

    // Another request moved the booking between the read and the update: answer for what it is now.
    private static async Task<Abandonment> LostRaceAsync(
        IWriterConnection writer, ReleaseTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        HeadRow? current = await GetHeadAsync(writer, target.BookingId, cancellationToken);
        ReleaseBookingResult? settled = await SettleWithoutMoveAsync(writer, current!, now, cancellationToken);
        if (settled != null)
        {
            return new Abandonment(settled, null);
        }

        return new Abandonment(NotUnpaid(current!), null);
    }

    private static ReleaseBookingResult? CheckOwner(HeadRow? head, Caller caller)
    {
        if (head is null)
        {
            return new ReleaseBookingResult { Outcome = ReleaseBookingOutcome.NotFound };
        }

        if (head.CustomerId != caller.AccountId)
        {
            return new ReleaseBookingResult { Outcome = ReleaseBookingOutcome.Forbidden };
        }

        return null;
    }

    // A booking already abandoned answers unchanged, and any booking that is not waiting for payment
    // is refused; only an unpaid booking needs the move. Null means the booking can be abandoned now.
    private static async Task<ReleaseBookingResult?> SettleWithoutMoveAsync(
        IWriterConnection writer, HeadRow head, DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool isAbandoned = head.Status == nameof(BookingStatus.Failed) && head.FailureReason == nameof(FailureReason.Abandoned);
        if (isAbandoned)
        {
            return await ToReleasedAsync(writer, head.BookingId, now, cancellationToken);
        }

        BookingStatus status = Enum.Parse<BookingStatus>(head.Status);
        if (!BookingRules.IsAwaitingPaymentResult(status))
        {
            return NotUnpaid(head);
        }

        return null;
    }

    private static ReleaseBookingResult NotUnpaid(HeadRow head)
    {
        return new ReleaseBookingResult
        {
            Outcome = ReleaseBookingOutcome.NotUnpaid,
            Status = Enum.Parse<BookingStatus>(head.Status),
        };
    }

    private static async Task<ReleaseBookingResult> ToReleasedAsync(
        IWriterConnection writer, Guid bookingId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(writer, bookingId, now, cancellationToken);
        return new ReleaseBookingResult { Outcome = ReleaseBookingOutcome.Released, Response = booking };
    }

    private static async Task<bool> MoveToAbandonedAsync(IWriterConnection writer, ReleaseTarget target, CancellationToken cancellationToken)
    {
        var parameters = new { target.BookingId, Reason = nameof(FailureReason.Abandoned) };
        int moved = await writer.Connection.ExecuteAsync(new CommandDefinition(
            AbandonSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        if (moved == 0)
        {
            return false;
        }

        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, target.BookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, target.CorrelationId);
        await recorder.RecordFailedAsync(context, FailureReason.Abandoned, cancellationToken);
        return true;
    }

    /// <summary>What abandoning decided: the answer and the trip whose seats are to be released, when something was abandoned.</summary>
    /// <param name="Result">The answer to the customer.</param>
    /// <param name="TripIdToRelease">The trip of the booking this request just abandoned; null when nothing changed.</param>
    private sealed record Abandonment(ReleaseBookingResult Result, Guid? TripIdToRelease);
}
