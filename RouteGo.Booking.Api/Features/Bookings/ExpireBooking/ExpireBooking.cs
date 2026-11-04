namespace RouteGo.Booking.Api.Features.Bookings.ExpireBooking;

/// <summary>
/// The data of Seat Inventory's SeatReleased event, read as it came: every member is optional so the
/// handler can say what is wrong with an event instead of the reader guessing. Seat Inventory also
/// sends where the seats went (<c>releasedTo</c>), which Booking ignores.
/// </summary>
internal sealed record SeatReleasedData
{
    public string? TripId { get; init; }

    public string? BookingId { get; init; }

    public List<string>? SeatNos { get; init; }

    /// <summary>HoldExpired, HoldReleased, BookingFailed, BookingCancelled or AdminReleased.</summary>
    public string? Reason { get; init; }
}
