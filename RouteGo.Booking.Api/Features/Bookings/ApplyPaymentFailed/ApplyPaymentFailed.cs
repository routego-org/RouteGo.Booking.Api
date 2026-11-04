namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentFailed;

/// <summary>
/// The data of Payment's PaymentFailed event, read as it came: every member is optional so the
/// handler can say what is wrong with an event instead of the reader guessing. Payment also sends the
/// payment identifier, which Booking ignores.
/// </summary>
internal sealed record PaymentFailedData
{
    public string? BookingId { get; init; }

    /// <summary>Payment's own words for why it failed, kept on the booking.</summary>
    public string? Reason { get; init; }
}
