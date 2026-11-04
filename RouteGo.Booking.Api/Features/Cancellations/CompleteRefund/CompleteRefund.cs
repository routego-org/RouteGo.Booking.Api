namespace RouteGo.Booking.Api.Features.Cancellations.CompleteRefund;

/// <summary>
/// The data of Payment's RefundCompleted event, read as it came: every member is optional so the
/// handler can say what is wrong with an event instead of the reader guessing. Payment also sends the
/// payment, the refund, the reason and the customer's email, which Booking ignores.
/// </summary>
internal sealed record RefundCompletedData
{
    public string? BookingId { get; init; }

    /// <summary>Whole rupees returned; a JSON number, read leniently.</summary>
    public decimal? Amount { get; init; }
}
