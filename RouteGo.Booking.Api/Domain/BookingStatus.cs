namespace RouteGo.Booking.Api.Domain;

/// <summary>Where a booking is in its life. A booking moves between these only along the steps the data model allows.</summary>
public enum BookingStatus
{
    /// <summary>The booking is recorded and Seat Inventory has not answered the hold yet.</summary>
    Initiated,

    /// <summary>Seat Inventory holds every seat; the payment has not started.</summary>
    SeatsHeld,

    /// <summary>The payment has started and the customer is on the payment page.</summary>
    PaymentPending,

    /// <summary>Paid, and Seat Inventory confirmed the seats.</summary>
    Confirmed,

    /// <summary>Ended without being confirmed; the failure reason says why.</summary>
    Failed,

    /// <summary>Seat Inventory released an unpaid booking's hold.</summary>
    Expired,

    /// <summary>A confirmed booking that was cancelled.</summary>
    Cancelled,
}
