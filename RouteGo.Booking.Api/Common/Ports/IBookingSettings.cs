namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>
/// The business settings, current within about a minute of a change and never older than the last
/// successful read. Handlers read <see cref="Current"/> once per operation so one operation never
/// mixes two versions of the settings.
/// </summary>
public interface IBookingSettings
{
    /// <summary>The settings now in force.</summary>
    BookingSettingsValues Current { get; }
}
