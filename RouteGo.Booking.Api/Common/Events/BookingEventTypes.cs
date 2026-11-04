namespace RouteGo.Booking.Api.Common.Events;

/// <summary>The names of the events Booking publishes, as the booking events page spells them.</summary>
public static class BookingEventTypes
{
    /// <summary>A paid booking's seats were confirmed.</summary>
    public const string BookingConfirmed = "BookingConfirmed";

    /// <summary>A confirmed booking moved to other seats of the same class.</summary>
    public const string BookingSeatsChanged = "BookingSeatsChanged";

    /// <summary>A booking ended without being confirmed.</summary>
    public const string BookingFailed = "BookingFailed";

    /// <summary>An unpaid booking's seats were released by Seat Inventory.</summary>
    public const string BookingExpired = "BookingExpired";

    /// <summary>A confirmed booking was cancelled.</summary>
    public const string BookingCancelled = "BookingCancelled";

    /// <summary>Booking asks Payment to return money.</summary>
    public const string RefundRequested = "RefundRequested";

    /// <summary>A cancellation, for the audit log.</summary>
    public const string AuditRecorded = "AuditRecorded";

    /// <summary>The currency of every amount.</summary>
    public const string Currency = "INR";
}
