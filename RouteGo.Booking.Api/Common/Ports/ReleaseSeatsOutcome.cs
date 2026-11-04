namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>How Seat Inventory answered a release request.</summary>
public enum ReleaseSeatsOutcome
{
    /// <summary>The booking's hold is freed; nothing held counts as freed.</summary>
    Released,

    /// <summary>The seats are already booked, so only a cancellation frees them.</summary>
    AlreadyConfirmed,

    /// <summary>Seat Inventory is down or gave an unusable answer.</summary>
    Unavailable,

    /// <summary>Seat Inventory did not answer in time.</summary>
    TimedOut,
}
