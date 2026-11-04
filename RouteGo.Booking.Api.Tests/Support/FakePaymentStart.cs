namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>One payment start the fake Payment received.</summary>
/// <param name="BookingId">The booking to pay for.</param>
/// <param name="Amount">The amount in whole rupees.</param>
/// <param name="CustomerEmail">The booking contact's email.</param>
public sealed record FakePaymentStart(Guid BookingId, long Amount, string CustomerEmail);
