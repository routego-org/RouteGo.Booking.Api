namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>The answer to a confirm request.</summary>
/// <param name="Outcome">How Seat Inventory answered.</param>
public sealed record ConfirmSeatsResult(ConfirmSeatsOutcome Outcome);
