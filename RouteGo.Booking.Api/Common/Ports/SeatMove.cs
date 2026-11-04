namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>One passenger's move from one seat label to another.</summary>
/// <param name="FromSeatNo">The seat the passenger leaves.</param>
/// <param name="ToSeatNo">The seat the passenger moves to.</param>
public sealed record SeatMove(string FromSeatNo, string ToSeatNo);
