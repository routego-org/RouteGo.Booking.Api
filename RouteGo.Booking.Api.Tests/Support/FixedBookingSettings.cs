namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>Settings a test sets directly, standing in for the SSM-backed ones; the specs' defaults until changed.</summary>
public sealed class FixedBookingSettings : IBookingSettings
{
    /// <summary>The settings in force; a test replaces them to change a rule.</summary>
    public BookingSettingsValues Current { get; set; } = BookingSettingsValues.CreateDefault();
}
