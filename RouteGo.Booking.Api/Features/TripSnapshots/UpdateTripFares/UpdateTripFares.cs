namespace RouteGo.Booking.Api.Features.TripSnapshots.UpdateTripFares;

/// <summary>One seat's new fare from Fleet's FareChanged data.</summary>
/// <param name="SeatNo">The seat label.</param>
/// <param name="Fare">The new fare in whole rupees.</param>
internal sealed record SeatFareMessage(string SeatNo, long Fare);

/// <summary>The part of Fleet's FareChanged data that Booking applies, checked and ready to store.</summary>
/// <param name="TripId">The re-priced trip.</param>
/// <param name="Seats">Every seat named with its new fare.</param>
internal sealed record FareChangedMessage(Guid TripId, IReadOnlyList<SeatFareMessage> Seats);

/// <summary>
/// Reads Fleet's FareChanged data by member name. Fleet repeats the class fares and each seat's class;
/// Booking prices by seat, so only the trip and each seat's label and fare are read.
/// </summary>
internal static class FareChangedReader
{
    /// <summary>The checked message, or null with a problem naming what is missing or unusable.</summary>
    public static FareChangedMessage? Read(JsonElement data, out string? problem)
    {
        problem = "the data is not a JSON object";
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Guid tripId = ReadTripId(data);
        IReadOnlyList<SeatFareMessage>? seats = ReadSeats(data);
        if (tripId == Guid.Empty || seats == null)
        {
            problem = "missing or unusable: tripId, or seats with a label and a whole fare each (labels not repeated)";
            return null;
        }

        problem = null;
        return new FareChangedMessage(tripId, seats);
    }

    private static Guid ReadTripId(JsonElement data)
    {
        bool found = data.TryGetProperty("tripId", out JsonElement member);
        if (!found || member.ValueKind != JsonValueKind.String)
        {
            return Guid.Empty;
        }

        Guid.TryParse(member.GetString(), out Guid tripId);
        return tripId;
    }

    private static IReadOnlyList<SeatFareMessage>? ReadSeats(JsonElement data)
    {
        bool found = data.TryGetProperty("seats", out JsonElement array);
        if (!found || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            return null;
        }

        var seats = new List<SeatFareMessage>();
        var labels = new HashSet<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            SeatFareMessage? seat = ReadSeat(item);
            if (seat == null || !labels.Add(seat.SeatNo))
            {
                return null;
            }

            seats.Add(seat);
        }

        return seats;
    }

    private static SeatFareMessage? ReadSeat(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool hasSeatNo = item.TryGetProperty("seatNo", out JsonElement seatNoMember);
        bool hasFare = item.TryGetProperty("fare", out JsonElement fareMember);
        if (!hasSeatNo || !hasFare || seatNoMember.ValueKind != JsonValueKind.String || fareMember.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        string? seatNo = seatNoMember.GetString();
        bool isWhole = fareMember.TryGetInt64(out long fare);
        if (string.IsNullOrWhiteSpace(seatNo) || !isWhole || fare < 0)
        {
            return null;
        }

        return new SeatFareMessage(seatNo, fare);
    }
}
