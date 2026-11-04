namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>One seat of a seeded booking with its passenger.</summary>
/// <param name="SeatNo">The seat label.</param>
/// <param name="PassengerName">The passenger's name.</param>
/// <param name="Fare">The fare the seat was sold at.</param>
public sealed record SeedPassenger(string SeatNo, string PassengerName, long Fare);
