namespace RouteGo.Booking.Api.Common.Events;

/// <summary>One seat of a booking with its passenger, as an event carries it.</summary>
public sealed class BookingEventSeat
{
    /// <summary>The seat label.</summary>
    public string SeatNo { get; init; } = string.Empty;

    /// <summary>The seat class the booking was made with.</summary>
    public string SeatClass { get; init; } = string.Empty;

    /// <summary>The fare the seat was sold at, whole rupees.</summary>
    public long Fare { get; init; }

    /// <summary>The passenger's name.</summary>
    public string PassengerName { get; init; } = string.Empty;

    /// <summary>The passenger's age in whole years.</summary>
    public int PassengerAge { get; init; }

    /// <summary>The passenger's gender: Male, Female or Other.</summary>
    public string PassengerGender { get; init; } = string.Empty;
}
