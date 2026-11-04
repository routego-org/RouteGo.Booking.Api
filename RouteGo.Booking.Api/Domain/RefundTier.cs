namespace RouteGo.Booking.Api.Domain;

/// <summary>One step of the refund schedule: from this many hours before departure, this share comes back.</summary>
/// <param name="HoursBefore">Whole hours before departure from which the tier applies.</param>
/// <param name="RefundPercent">The share refunded, 0 to 100.</param>
public sealed record RefundTier(int HoursBefore, int RefundPercent);
