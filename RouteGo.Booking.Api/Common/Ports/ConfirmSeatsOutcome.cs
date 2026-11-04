namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>How Seat Inventory answered a confirm request.</summary>
public enum ConfirmSeatsOutcome
{
    /// <summary>The booking's seats are booked, now or already.</summary>
    Confirmed,

    /// <summary>The booking holds no seats: released, expired or never held.</summary>
    HoldNotActive,

    /// <summary>The trip is cancelled, so its hold cannot be confirmed.</summary>
    TripCancelled,

    /// <summary>Seat Inventory is down or gave an unusable answer; the confirm may be tried again.</summary>
    Unavailable,

    /// <summary>Seat Inventory did not answer in time; the confirm may be tried again.</summary>
    TimedOut,
}
