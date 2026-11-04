namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>One seat of a seeded trip snapshot.</summary>
/// <param name="SeatNo">The seat label.</param>
/// <param name="SeatClass">The seat class.</param>
/// <param name="Fare">The seat's current fare in whole rupees.</param>
public sealed record SeedSeat(string SeatNo, string SeatClass, long Fare);
