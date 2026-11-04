namespace RouteGo.Booking.Api.Features.Cancellations.CancelTripBookings;

/// <summary>The data of Fleet's TripCancelled event, checked and ready to apply.</summary>
/// <param name="TripId">The cancelled trip.</param>
/// <param name="Reason">Fleet's reason for the cancellation.</param>
internal sealed record TripCancelledMessage(Guid TripId, string Reason);

/// <summary>Reads Fleet's TripCancelled data by member name.</summary>
internal static class TripCancelledReader
{
    // Both the snapshot and the cancellation record hold the reason in 500 characters.
    private const int MaxReasonLength = 500;

    /// <summary>The checked message, or null with a problem naming what is missing or unusable.</summary>
    public static TripCancelledMessage? Read(JsonElement data, out string? problem)
    {
        problem = "the data is not a JSON object";
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? tripText = ReadText(data, "tripId");
        string? reason = ReadText(data, "reason");
        bool hasTrip = Guid.TryParse(tripText, out Guid tripId) && tripId != Guid.Empty;
        if (!hasTrip || reason == null || reason.Length > MaxReasonLength)
        {
            problem = $"missing or unusable: tripId, or a reason of 1 to {MaxReasonLength} characters (trip {tripText})";
            return null;
        }

        problem = null;
        return new TripCancelledMessage(tripId, reason);
    }

    private static string? ReadText(JsonElement data, string name)
    {
        bool found = data.TryGetProperty(name, out JsonElement member);
        if (!found || member.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = member.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text;
    }
}
