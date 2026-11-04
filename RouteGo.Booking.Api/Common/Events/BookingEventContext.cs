namespace RouteGo.Booking.Api.Common.Events;

/// <summary>
/// Everything an event about one booking can need: the booking and its contact, its seats with their
/// passengers, and the trip snapshot's route, times, bus and operator. Loaded inside the transaction
/// that changed the booking, after the change, so an event always describes the booking as recorded.
/// </summary>
public sealed class BookingEventContext
{
    public Guid BookingId { get; init; }

    public string CustomerId { get; init; } = string.Empty;

    public string ContactName { get; init; } = string.Empty;

    public string ContactEmail { get; init; } = string.Empty;

    public Guid TripId { get; init; }

    public string Status { get; init; } = string.Empty;

    public long TotalAmount { get; init; }

    public Guid? PaymentId { get; init; }

    public DateTimeOffset? ConfirmedAt { get; init; }

    public int SeatChangeCount { get; init; }

    public string OperatorId { get; init; } = string.Empty;

    public string OperatorName { get; init; } = string.Empty;

    public string OperatorEmail { get; init; } = string.Empty;

    public string SourceCityName { get; init; } = string.Empty;

    public string DestinationCityName { get; init; } = string.Empty;

    /// <summary>The service date in IST, written yyyy-MM-dd.</summary>
    public string ServiceDate { get; init; } = string.Empty;

    public DateTimeOffset DepartureAt { get; init; }

    public DateTimeOffset ArrivalAt { get; init; }

    public string BusName { get; init; } = string.Empty;

    public string BusType { get; init; } = string.Empty;

    public bool IsAc { get; init; }

    /// <summary>The booking's seats, ordered by seat label.</summary>
    public IReadOnlyList<BookingEventSeat> Seats { get; init; } = Array.Empty<BookingEventSeat>();
}
