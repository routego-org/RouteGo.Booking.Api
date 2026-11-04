namespace RouteGo.Booking.Api.Infrastructure.Settings;

/// <summary>
/// Turns the raw parameter values found under the settings path into <see cref="BookingSettingsValues"/>.
/// A value that is missing keeps the setting as it was; a value that is present but unusable is
/// reported and also keeps the setting as it was, so one bad edit never resets the business rules.
/// </summary>
public static class BookingSettingsReader
{
    /// <summary>Parameter name of the hold length in seconds.</summary>
    public const string HoldSecondsName = "hold-seconds";

    /// <summary>Parameter name of the seats-per-booking limit.</summary>
    public const string MaxSeatsName = "max-seats";

    /// <summary>Parameter name of the booking cutoff in minutes before departure.</summary>
    public const string CutoffMinutesName = "min-lead-minutes";

    /// <summary>Parameter name of the seat changes allowed per booking.</summary>
    public const string SeatChangeLimitName = "seat-change-limit";

    /// <summary>Parameter name of the days after departure an admin may force-cancel.</summary>
    public const string ForceCancelWindowDaysName = "force-cancel-window-days";

    /// <summary>Parameter name of the refund schedule, a JSON array of hoursBefore and refundPercent.</summary>
    public const string CancellationTiersName = "cancellation-tiers";

    /// <summary>The settings after applying every usable parameter to the previous settings.</summary>
    /// <param name="previous">The settings in force before this read.</param>
    /// <param name="parameters">Parameter values by short name (the last segment of the parameter's path).</param>
    /// <param name="problems">Receives one sentence for each parameter that is present but unusable.</param>
    public static BookingSettingsValues Read(
        BookingSettingsValues previous, IReadOnlyDictionary<string, string> parameters, List<string> problems)
    {
        return new BookingSettingsValues
        {
            HoldSeconds = ReadWhole(parameters, HoldSecondsName, previous.HoldSeconds, BookingRules.IsValidHoldSeconds, problems),
            MaxSeats = ReadWhole(parameters, MaxSeatsName, previous.MaxSeats, BookingRules.IsValidMaxSeats, problems),
            CutoffMinutes = ReadWhole(parameters, CutoffMinutesName, previous.CutoffMinutes, IsNotNegative, problems),
            SeatChangeLimit = ReadWhole(parameters, SeatChangeLimitName, previous.SeatChangeLimit, IsNotNegative, problems),
            ForceCancelWindowDays = ReadWhole(parameters, ForceCancelWindowDaysName, previous.ForceCancelWindowDays, IsNotNegative, problems),
            CancellationTiers = ReadTiers(parameters, previous.CancellationTiers, problems),
        };
    }

    private static int ReadWhole(
        IReadOnlyDictionary<string, string> parameters, string name, int previous, Func<int, bool> isUsable, List<string> problems)
    {
        bool isPresent = parameters.TryGetValue(name, out string? text);
        if (!isPresent)
        {
            return previous;
        }

        bool isNumber = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value);
        if (!isNumber || !isUsable(value))
        {
            problems.Add($"{name} is '{text}', which is not a usable value; keeping {previous}");
            return previous;
        }

        return value;
    }

    private static IReadOnlyList<RefundTier> ReadTiers(
        IReadOnlyDictionary<string, string> parameters, IReadOnlyList<RefundTier> previous, List<string> problems)
    {
        bool isPresent = parameters.TryGetValue(CancellationTiersName, out string? text);
        if (!isPresent)
        {
            return previous;
        }

        List<RefundTier>? tiers = ParseTiers(text!);
        if (tiers is null || !RefundRules.IsValidSchedule(tiers))
        {
            problems.Add($"{CancellationTiersName} is not a usable refund schedule; keeping the previous one");
            return previous;
        }

        return tiers;
    }

    private static List<RefundTier>? ParseTiers(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RefundTier>>(json, WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsNotNegative(int value)
    {
        return value >= 0;
    }
}
