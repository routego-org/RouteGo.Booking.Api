namespace RouteGo.Booking.Api.Domain;

/// <summary>Why money comes back to a customer.</summary>
public enum RefundReason
{
    /// <summary>A confirmed booking was cancelled, by the customer, an admin or the operator.</summary>
    Cancellation,

    /// <summary>The payment succeeded after the booking had ended.</summary>
    LatePayment,

    /// <summary>The payment succeeded but the seats were lost.</summary>
    SeatsLost,

    /// <summary>The payment succeeded but the trip was cancelled.</summary>
    TripCancelled,
}
