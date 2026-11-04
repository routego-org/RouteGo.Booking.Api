namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>The answer to a release request.</summary>
/// <param name="Outcome">How Seat Inventory answered.</param>
public sealed record ReleaseSeatsResult(ReleaseSeatsOutcome Outcome);
