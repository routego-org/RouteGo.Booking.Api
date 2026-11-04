namespace RouteGo.Booking.Api.Common;

/// <summary>
/// Turns the UTC times the database driver hands back as <see cref="DateTime"/> into
/// <see cref="DateTimeOffset"/> values. Every time Booking stores is UTC.
/// </summary>
public static class UtcTimes
{
    /// <summary>The same instant as a UTC offset time.</summary>
    public static DateTimeOffset ToOffset(DateTime utcTime)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc));
    }

    /// <summary>The same instant as a UTC offset time, or null when there is none.</summary>
    public static DateTimeOffset? ToOffset(DateTime? utcTime)
    {
        if (utcTime is null)
        {
            return null;
        }

        return ToOffset(utcTime.Value);
    }
}
