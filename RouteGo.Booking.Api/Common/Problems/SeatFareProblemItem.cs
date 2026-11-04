namespace RouteGo.Booking.Api.Common.Problems;

/// <summary>One seat and its current fare, named by the refusal that tells a customer the price moved.</summary>
/// <param name="SeatNo">The seat label.</param>
/// <param name="Fare">The seat's current fare in whole rupees.</param>
public sealed record SeatFareProblemItem(string SeatNo, int Fare);
