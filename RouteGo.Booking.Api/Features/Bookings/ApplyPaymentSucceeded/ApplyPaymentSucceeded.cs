namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentSucceeded;

/// <summary>
/// The data of Payment's PaymentSucceeded event, read as it came: every member is optional so the
/// handler can say what is wrong with an event instead of the reader guessing. Payment also sends the
/// currency, which is always INR and ignored.
/// </summary>
internal sealed record PaymentSucceededData
{
    public string? BookingId { get; init; }

    public string? PaymentId { get; init; }

    /// <summary>Whole rupees paid; a JSON number, read leniently.</summary>
    public decimal? Amount { get; init; }
}
