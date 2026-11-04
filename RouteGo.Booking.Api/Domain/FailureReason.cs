namespace RouteGo.Booking.Api.Domain;

/// <summary>Why a booking ended as failed.</summary>
public enum FailureReason
{
    /// <summary>Seat Inventory refused the hold: some seats were not free.</summary>
    SeatsUnavailable,

    /// <summary>Seat Inventory could not be reached, or gave an unusable answer, while holding.</summary>
    InventoryUnavailable,

    /// <summary>Payment could not start the payment.</summary>
    PaymentUnavailable,

    /// <summary>Payment reported the payment failed.</summary>
    PaymentDeclined,

    /// <summary>The customer gave up before paying.</summary>
    Abandoned,

    /// <summary>Fleet cancelled the trip.</summary>
    TripCancelled,

    /// <summary>The payment succeeded but Seat Inventory no longer held the seats.</summary>
    SeatsLost,
}
