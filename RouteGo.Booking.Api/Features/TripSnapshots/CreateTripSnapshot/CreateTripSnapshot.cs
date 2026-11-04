using System.Globalization;

namespace RouteGo.Booking.Api.Features.TripSnapshots.CreateTripSnapshot;

/// <summary>One seat of the announced trip, as Fleet gave it.</summary>
/// <param name="SeatNo">The seat label.</param>
/// <param name="SeatClass">The seat class name.</param>
/// <param name="Fare">The fare in whole rupees.</param>
internal sealed record TripSeatMessage(string SeatNo, string SeatClass, long Fare);

/// <summary>The part of Fleet's TripScheduled data that Booking keeps, checked and ready to store.</summary>
internal sealed class TripScheduledMessage
{
    public required Guid TripId { get; init; }

    public required string OperatorId { get; init; }

    public required string OperatorName { get; init; }

    public required string OperatorEmail { get; init; }

    public required string BusName { get; init; }

    public required string BusType { get; init; }

    public required bool IsAc { get; init; }

    public required string SourceCityName { get; init; }

    public required string DestinationCityName { get; init; }

    public required DateOnly ServiceDate { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    public required DateTimeOffset ArrivalAt { get; init; }

    public required IReadOnlyList<TripSeatMessage> Seats { get; init; }
}

/// <summary>
/// Reads Fleet's TripScheduled data by member name. The seat class member is called <c>class</c>, which
/// no record property can be without a name override, so members are read from the JSON directly. Fleet
/// sends the cities as objects; their name member is read as <c>cityName</c>, or <c>name</c> as Fleet's
/// event writes it today.
/// </summary>
internal static class TripScheduledReader
{
    private const int MaxOperatorIdLength = 64;
    private const int MaxOperatorNameLength = 200;
    private const int MaxOperatorEmailLength = 254;
    private const int MaxCityNameLength = 100;
    private const int MaxBusNameLength = 200;
    private const int MaxSeatNoLength = 16;
    private const int MaxSeatClassLength = 64;
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string[] _busTypes = { "Seater", "Sleeper", "SemiSleeper" };

    /// <summary>The checked message, or null with a problem naming every member that is missing or unusable.</summary>
    public static TripScheduledMessage? Read(JsonElement data, out string? problem)
    {
        problem = "the data is not a JSON object";
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var unusable = new List<string>();
        var message = new TripScheduledMessage
        {
            TripId = ReadGuid(data, "tripId", unusable),
            OperatorId = ReadText(data, "operatorId", MaxOperatorIdLength, unusable),
            OperatorName = ReadText(data, "operatorName", MaxOperatorNameLength, unusable),
            OperatorEmail = ReadText(data, "operatorEmail", MaxOperatorEmailLength, unusable),
            BusName = ReadText(data, "busName", MaxBusNameLength, unusable),
            BusType = ReadBusType(data, unusable),
            IsAc = ReadBool(data, "isAc", unusable),
            SourceCityName = ReadCityName(data, "source", unusable),
            DestinationCityName = ReadCityName(data, "destination", unusable),
            ServiceDate = ReadDate(data, "serviceDate", unusable),
            DepartureAt = ReadTime(data, "departureUtc", unusable),
            ArrivalAt = ReadTime(data, "arrivalUtc", unusable),
            Seats = ReadSeats(data, unusable),
        };
        if (unusable.Count > 0)
        {
            problem = "missing or unusable: " + string.Join(", ", unusable);
            return null;
        }

        problem = null;
        return message;
    }

    private static Guid ReadGuid(JsonElement data, string name, List<string> unusable)
    {
        string? text = ReadOptionalText(data, name);
        bool isGuid = Guid.TryParse(text, out Guid value);
        if (!isGuid || value == Guid.Empty)
        {
            unusable.Add(name);
        }

        return value;
    }

    private static string ReadText(JsonElement data, string name, int maxLength, List<string> unusable)
    {
        string? text = ReadOptionalText(data, name);
        if (text == null || text.Length > maxLength)
        {
            unusable.Add(name);
            return string.Empty;
        }

        return text;
    }

    private static string? ReadOptionalText(JsonElement data, string name)
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

    private static string ReadBusType(JsonElement data, List<string> unusable)
    {
        string? text = ReadOptionalText(data, "busType");
        if (text == null || !_busTypes.Contains(text))
        {
            unusable.Add("busType");
            return string.Empty;
        }

        return text;
    }

    private static bool ReadBool(JsonElement data, string name, List<string> unusable)
    {
        bool found = data.TryGetProperty(name, out JsonElement member);
        if (!found || (member.ValueKind != JsonValueKind.True && member.ValueKind != JsonValueKind.False))
        {
            unusable.Add(name);
            return false;
        }

        return member.GetBoolean();
    }

    private static string ReadCityName(JsonElement data, string name, List<string> unusable)
    {
        bool found = data.TryGetProperty(name, out JsonElement city);
        if (!found || city.ValueKind != JsonValueKind.Object)
        {
            unusable.Add(name);
            return string.Empty;
        }

        string? cityName = ReadOptionalText(city, "cityName");
        if (cityName == null)
        {
            cityName = ReadOptionalText(city, "name");
        }

        if (cityName == null || cityName.Length > MaxCityNameLength)
        {
            unusable.Add(name);
            return string.Empty;
        }

        return cityName;
    }

    private static DateOnly ReadDate(JsonElement data, string name, List<string> unusable)
    {
        string? text = ReadOptionalText(data, name);
        bool isDate = DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly value);
        if (!isDate)
        {
            unusable.Add(name);
        }

        return value;
    }

    private static DateTimeOffset ReadTime(JsonElement data, string name, List<string> unusable)
    {
        string? text = ReadOptionalText(data, name);
        DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        bool isTime = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out DateTimeOffset value);
        if (!isTime)
        {
            unusable.Add(name);
        }

        return value;
    }

    private static IReadOnlyList<TripSeatMessage> ReadSeats(JsonElement data, List<string> unusable)
    {
        var seats = new List<TripSeatMessage>();
        bool found = data.TryGetProperty("seats", out JsonElement array);
        if (!found || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            unusable.Add("seats");
            return seats;
        }

        var labels = new HashSet<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            TripSeatMessage? seat = ReadSeat(item);
            if (seat == null || !labels.Add(seat.SeatNo))
            {
                unusable.Add("seats (a seat has no label, class or fare, or a label is repeated)");
                return seats;
            }

            seats.Add(seat);
        }

        return seats;
    }

    private static TripSeatMessage? ReadSeat(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? seatNo = ReadOptionalText(item, "seatNo");
        string? seatClass = ReadOptionalText(item, "class");
        bool hasFare = item.TryGetProperty("fare", out JsonElement fareMember);
        long fare = 0;
        bool isWhole = hasFare && fareMember.ValueKind == JsonValueKind.Number && fareMember.TryGetInt64(out fare);
        if (seatNo == null || seatClass == null || !isWhole || fare < 0)
        {
            return null;
        }

        if (seatNo.Length > MaxSeatNoLength || seatClass.Length > MaxSeatClassLength)
        {
            return null;
        }

        return new TripSeatMessage(seatNo, seatClass, fare);
    }
}
