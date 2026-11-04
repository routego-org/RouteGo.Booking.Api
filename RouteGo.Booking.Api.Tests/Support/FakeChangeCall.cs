namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>One seat change request the fake Seat Inventory received.</summary>
/// <param name="TripId">The trip.</param>
/// <param name="BookingId">The booking whose seats were to move.</param>
/// <param name="Moves">The moves asked for.</param>
public sealed record FakeChangeCall(Guid TripId, Guid BookingId, IReadOnlyList<SeatMove> Moves);
