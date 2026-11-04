namespace RouteGo.Booking.Api.Common.Events;

/// <summary>What a cancellation adds to the booking's own facts in the BookingCancelled event.</summary>
public sealed record CancelledEventInfo
{
    /// <summary>CustomerCancelled, AdminCancelled or OperatorCancelled.</summary>
    public required string Kind { get; init; }

    /// <summary>The admin's reason or Fleet's reason; null for a customer cancellation.</summary>
    public string? Reason { get; init; }

    /// <summary>The share refunded, 0 to 100.</summary>
    public required int RefundPercent { get; init; }

    /// <summary>The amount refunded in whole rupees, rounded down; 0 when no refund is due.</summary>
    public required long RefundAmount { get; init; }
}
