namespace RouteGo.Booking.Api.Features.Bookings.ListTripPassengers;

public sealed class ListTripPassengersHandler
{
    // A snapshot that has only heard about fares has no operator yet; Booking cannot tell whose trip it is,
    // so it is treated as unknown. The seat labels of a booking that moved seats are already rewritten, so
    // reading booking_seats always gives the current seat.
    private const string SelectPassengersSql = """
        SELECT trip_id, operator_id, operator_name, source_city_name, destination_city_name, service_date,
               departure_at, arrival_at, bus_name, bus_type
        FROM trip_snapshots.trip_snapshots
        WHERE trip_id = @TripId AND operator_id IS NOT NULL;

        SELECT s.seat_no, s.seat_class, s.booking_id, s.passenger_name, s.passenger_age, s.passenger_gender
        FROM bookings.booking_seats s
        JOIN bookings.bookings b ON b.booking_id = s.booking_id
        WHERE b.trip_id = @TripId AND b.status = 'Confirmed'
        ORDER BY s.seat_no, s.booking_id;
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public ListTripPassengersHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Lists the passengers of the confirmed bookings on a trip, for the operator that owns the trip.</summary>
    public async Task<ListTripPassengersResult> HandleAsync(Guid tripId, Caller caller, CancellationToken cancellationToken)
    {
        TripData? data = await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, tripId, cancellationToken), cancellationToken);
        if (data is null)
        {
            return new ListTripPassengersResult(ListTripPassengersOutcome.NotFound);
        }

        if (data.Trip.OperatorId != caller.OperatorId)
        {
            return new ListTripPassengersResult(ListTripPassengersOutcome.Forbidden);
        }

        return new ListTripPassengersResult(ListTripPassengersOutcome.Listed, ToList(data));
    }

    private static async Task<TripData?> ReadAsync(DbConnection connection, Guid tripId, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(SelectPassengersSql, new { TripId = tripId }, cancellationToken: cancellationToken);
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(command);
        TripRow? trip = await results.ReadSingleOrDefaultAsync<TripRow>();
        IEnumerable<PassengerRow> passengers = await results.ReadAsync<PassengerRow>();
        if (trip is null)
        {
            return null;
        }

        return new TripData(trip, passengers.ToList());
    }

    private static TripPassengerList ToList(TripData data)
    {
        var passengers = new List<TripPassenger>();
        foreach (PassengerRow row in data.Passengers)
        {
            passengers.Add(ToPassenger(row));
        }

        return new TripPassengerList
        {
            Trip = ToTrip(data.Trip),
            SeatsBooked = passengers.Count,
            Passengers = passengers,
        };
    }

    private static TripPassenger ToPassenger(PassengerRow row)
    {
        return new TripPassenger
        {
            SeatNo = row.SeatNo,
            SeatClass = row.SeatClass,
            BookingId = row.BookingId,
            PassengerName = row.PassengerName,
            PassengerAge = row.PassengerAge,
            PassengerGender = Enum.Parse<PassengerGender>(row.PassengerGender),
        };
    }

    private static TripSummary ToTrip(TripRow row)
    {
        return new TripSummary
        {
            TripId = row.TripId,
            SourceCityName = row.SourceCityName,
            DestinationCityName = row.DestinationCityName,
            ServiceDate = DateOnly.FromDateTime(row.ServiceDate),
            DepartureAt = UtcTimes.ToOffset(row.DepartureAt),
            ArrivalAt = UtcTimes.ToOffset(row.ArrivalAt),
            BusName = row.BusName,
            BusType = Enum.Parse<BusType>(row.BusType),
            OperatorName = row.OperatorName,
        };
    }

    private sealed record TripData(TripRow Trip, IReadOnlyList<PassengerRow> Passengers);

    private sealed class TripRow
    {
        public Guid TripId { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public string OperatorName { get; init; } = string.Empty;

        public string SourceCityName { get; init; } = string.Empty;

        public string DestinationCityName { get; init; } = string.Empty;

        public DateTime ServiceDate { get; init; }

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }

        public string BusName { get; init; } = string.Empty;

        public string BusType { get; init; } = string.Empty;
    }

    private sealed class PassengerRow
    {
        public string SeatNo { get; init; } = string.Empty;

        public string SeatClass { get; init; } = string.Empty;

        public Guid BookingId { get; init; }

        public string PassengerName { get; init; } = string.Empty;

        public int PassengerAge { get; init; }

        public string PassengerGender { get; init; } = string.Empty;
    }
}
