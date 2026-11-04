namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>
/// What Booking asks of Seat Inventory, each call answering at once. Every method returns a named
/// outcome: no status code or HTTP type leaks out, and an unreachable or unusable Seat Inventory is
/// an outcome, not an exception, because callers decide what an outage means for their booking.
/// Hold, confirm, release and seat change are all repeat-safe on Seat Inventory's side.
/// </summary>
public interface ISeatInventoryClient
{
    /// <summary>Holds every named seat for the booking, all or nothing.</summary>
    Task<HoldSeatsResult> HoldSeatsAsync(
        Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken);

    /// <summary>Turns the booking's paid hold into booked seats.</summary>
    Task<ConfirmSeatsResult> ConfirmSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken);

    /// <summary>Frees the booking's unpaid hold at once.</summary>
    Task<ReleaseSeatsResult> ReleaseSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken);

    /// <summary>Moves a confirmed booking's seats to other free seats of the same class, all or nothing.</summary>
    Task<ChangeSeatsResult> ChangeSeatsAsync(
        Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken);
}
