namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>The answer to a hold request: the outcome and the facts the outcome names.</summary>
public sealed record HoldSeatsResult
{
    /// <summary>How Seat Inventory answered.</summary>
    public required HoldSeatsOutcome Outcome { get; init; }

    /// <summary>When the hold ends unless confirmed; present when held.</summary>
    public DateTimeOffset? HoldExpiresAt { get; init; }

    /// <summary>The seats Seat Inventory named as unavailable or unknown, for those outcomes.</summary>
    public IReadOnlyList<string> NamedSeats { get; init; } = Array.Empty<string>();
}
