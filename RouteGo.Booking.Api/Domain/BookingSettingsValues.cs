namespace RouteGo.Booking.Api.Domain;

/// <summary>
/// The business settings that can change without a release: how long seats are held, how many seats
/// a booking may have, when booking closes, how often seats may change, how long after departure an
/// admin may still force-cancel, and the refund schedule. Immutable, so one snapshot is always
/// internally consistent.
/// </summary>
public sealed record BookingSettingsValues
{
    /// <summary>Seconds Seat Inventory holds seats while the customer pays.</summary>
    public required int HoldSeconds { get; init; }

    /// <summary>Most seats one booking may have.</summary>
    public required int MaxSeats { get; init; }

    /// <summary>Minutes before departure at which new bookings and seat changes close.</summary>
    public required int CutoffMinutes { get; init; }

    /// <summary>Most seat changes one booking may have.</summary>
    public required int SeatChangeLimit { get; init; }

    /// <summary>Days after departure during which an admin may still force-cancel a booking.</summary>
    public required int ForceCancelWindowDays { get; init; }

    /// <summary>The refund schedule, in any order.</summary>
    public required IReadOnlyList<RefundTier> CancellationTiers { get; init; }

    /// <summary>The settings the specs name as defaults, used for any setting nobody has set.</summary>
    public static BookingSettingsValues CreateDefault()
    {
        var tiers = new List<RefundTier>
        {
            new RefundTier(24, 90),
            new RefundTier(6, 50),
            new RefundTier(0, 0),
        };
        return new BookingSettingsValues
        {
            HoldSeconds = 600,
            MaxSeats = BookingRules.MaxSeatsCeiling,
            CutoffMinutes = 30,
            SeatChangeLimit = 3,
            ForceCancelWindowDays = 7,
            CancellationTiers = tiers,
        };
    }
}
