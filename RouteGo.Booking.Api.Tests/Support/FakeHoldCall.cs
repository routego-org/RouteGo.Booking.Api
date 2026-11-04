namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>One hold request the fake Seat Inventory received.</summary>
/// <param name="TripId">The trip.</param>
/// <param name="BookingId">The booking the seats were held for.</param>
/// <param name="SeatNos">The seats asked for.</param>
/// <param name="HoldSeconds">The hold length asked for.</param>
/// <param name="CorrelationId">The correlation ID the call carried.</param>
public sealed record FakeHoldCall(Guid TripId, Guid BookingId, IReadOnlyList<string> SeatNos, int HoldSeconds, string CorrelationId);
