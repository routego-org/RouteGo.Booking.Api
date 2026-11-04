namespace RouteGo.Booking.Api.Common.Problems;

/// <summary>One seat move named by a refusal, so the customer can rebuild the seat change.</summary>
/// <param name="FromSeatNo">The seat the passenger would leave.</param>
/// <param name="ToSeatNo">The seat the passenger would move to.</param>
public sealed record SeatMoveProblemItem(string FromSeatNo, string ToSeatNo);
