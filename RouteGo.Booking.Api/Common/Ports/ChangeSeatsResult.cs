namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>The answer to a seat change request: the outcome and the facts the outcome names.</summary>
public sealed record ChangeSeatsResult
{
    /// <summary>How Seat Inventory answered.</summary>
    public required ChangeSeatsOutcome Outcome { get; init; }

    /// <summary>The unavailable, unknown or not-owned seats Seat Inventory named, for those outcomes.</summary>
    public IReadOnlyList<string> NamedSeats { get; init; } = Array.Empty<string>();

    /// <summary>The moves to a seat of another class, for the class mismatch outcome.</summary>
    public IReadOnlyList<SeatMove> NamedMoves { get; init; } = Array.Empty<SeatMove>();
}
