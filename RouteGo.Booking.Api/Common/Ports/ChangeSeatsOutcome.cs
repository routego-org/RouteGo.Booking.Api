namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>How Seat Inventory answered a seat change request.</summary>
public enum ChangeSeatsOutcome
{
    /// <summary>Every seat moved, now or by an earlier identical request.</summary>
    Changed,

    /// <summary>Some to-seats are not free; nothing moved.</summary>
    SeatsUnavailable,

    /// <summary>Seat Inventory has no seats with some of these labels; nothing moved.</summary>
    UnknownSeats,

    /// <summary>Some from-seats are not booked by this booking; nothing moved.</summary>
    SeatsNotOwned,

    /// <summary>Some moves go to a seat of another class; nothing moved.</summary>
    SeatClassMismatch,

    /// <summary>Seat Inventory knows the trip as cancelled; nothing moved.</summary>
    TripCancelled,

    /// <summary>Seat Inventory refused for another reason: unknown or departed trip.</summary>
    Refused,

    /// <summary>Seat Inventory is down or gave an unusable answer; nothing is known to have moved.</summary>
    Unavailable,

    /// <summary>Seat Inventory did not answer in time; the change may or may not have happened.</summary>
    TimedOut,
}
