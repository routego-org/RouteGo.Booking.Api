namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>How Seat Inventory answered a hold request.</summary>
public enum HoldSeatsOutcome
{
    /// <summary>Every seat is held for the booking, now or by an earlier identical request.</summary>
    Held,

    /// <summary>Some seats are not free; nothing is held.</summary>
    SeatsUnavailable,

    /// <summary>Seat Inventory knows the trip as cancelled; nothing is held.</summary>
    TripCancelled,

    /// <summary>Seat Inventory has no seats with some of these labels; nothing is held.</summary>
    UnknownSeats,

    /// <summary>Seat Inventory refused for a reason that is not the customer's to fix: unknown or departed trip, a booking that already has seats, a hold too long.</summary>
    Refused,

    /// <summary>Seat Inventory is down or gave an unusable answer.</summary>
    Unavailable,

    /// <summary>Seat Inventory did not answer in time; the hold may or may not exist.</summary>
    TimedOut,
}
