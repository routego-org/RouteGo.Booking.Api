namespace RouteGo.Booking.Api.Domain;

/// <summary>The rules about what a booking may contain and when it may be made or changed. Pure: no clock, no I/O.</summary>
public static class BookingRules
{
    /// <summary>Seat Inventory's own limit; the seats-per-booking setting may never exceed it.</summary>
    public const int MaxSeatsCeiling = 6;

    /// <summary>Seat Inventory's hold ceiling in seconds; the hold-length setting may never exceed it.</summary>
    public const int MaxHoldSeconds = 900;

    /// <summary>Fewest characters in a contact or passenger name.</summary>
    public const int MinNameLength = 2;

    /// <summary>Most characters in a contact or passenger name.</summary>
    public const int MaxNameLength = 60;

    /// <summary>Youngest passenger age, in whole years.</summary>
    public const int MinPassengerAge = 0;

    /// <summary>Oldest passenger age, in whole years.</summary>
    public const int MaxPassengerAge = 120;

    /// <summary>Whether a contact or passenger name has an acceptable length.</summary>
    public static bool IsValidName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        int length = name.Trim().Length;
        return length >= MinNameLength && length <= MaxNameLength;
    }

    /// <summary>Whether an age is a whole number of years in range.</summary>
    public static bool IsValidPassengerAge(int? age)
    {
        if (age is null)
        {
            return false;
        }

        return age.Value >= MinPassengerAge && age.Value <= MaxPassengerAge;
    }

    /// <summary>Whether text names one of the genders, ignoring letter case.</summary>
    public static bool IsValidGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender))
        {
            return false;
        }

        bool isNumber = int.TryParse(gender, out _);
        if (isNumber)
        {
            return false;
        }

        return Enum.TryParse(gender, true, out PassengerGender _);
    }

    /// <summary>Reads a gender the wire spells in any letter case into the enum.</summary>
    public static PassengerGender ParseGender(string gender)
    {
        return Enum.Parse<PassengerGender>(gender, true);
    }

    /// <summary>
    /// Whether booking or seat change has closed: less than the cutoff remains before departure, or
    /// departure has passed. Exactly at the cutoff is still open.
    /// </summary>
    public static bool IsBookingClosed(DateTimeOffset departureAt, DateTimeOffset now, int cutoffMinutes)
    {
        TimeSpan remaining = departureAt - now;
        return remaining < TimeSpan.FromMinutes(cutoffMinutes);
    }

    /// <summary>Whether the trip has departed: its departure time has passed.</summary>
    public static bool HasDeparted(DateTimeOffset departureAt, DateTimeOffset now)
    {
        return now > departureAt;
    }

    /// <summary>Whether the booking is still waiting for payment, so its seats may yet be held, paid or released.</summary>
    public static bool IsUnpaid(BookingStatus status)
    {
        bool isInitiated = status == BookingStatus.Initiated;
        bool isHeld = status == BookingStatus.SeatsHeld;
        bool isPending = status == BookingStatus.PaymentPending;
        return isInitiated || isHeld || isPending;
    }

    /// <summary>Whether the booking is waiting for the payment result: seats held, payment possibly started.</summary>
    public static bool IsAwaitingPaymentResult(BookingStatus status)
    {
        return status == BookingStatus.SeatsHeld || status == BookingStatus.PaymentPending;
    }

    /// <summary>
    /// Whether the settings' seat limit is usable: at least one seat and never above Seat Inventory's
    /// own limit.
    /// </summary>
    public static bool IsValidMaxSeats(int maxSeats)
    {
        return maxSeats >= 1 && maxSeats <= MaxSeatsCeiling;
    }

    /// <summary>Whether the settings' hold length is usable: at least a second and never above Seat Inventory's ceiling.</summary>
    public static bool IsValidHoldSeconds(int holdSeconds)
    {
        return holdSeconds >= 1 && holdSeconds <= MaxHoldSeconds;
    }
}
