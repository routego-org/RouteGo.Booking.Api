using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.TripSnapshots.CreateTripSnapshot;

[Collection(DatabaseCollection.Name)]
public sealed class CreateTripSnapshotTests
{
    private const string SnapshotSql = """
        SELECT operator_id, operator_name, operator_email, source_city_name, destination_city_name, bus_name, bus_type,
               is_ac, is_cancelled, cancelled_at IS NOT NULL AS has_cancelled_at, to_char(service_date, 'YYYY-MM-DD') AS service_date,
               departure_at, arrival_at
        FROM trip_snapshots.trip_snapshots WHERE trip_id = @TripId
        """;

    private const string SeatsSql = "SELECT seat_no, seat_class, fare FROM trip_snapshots.trip_snapshot_seats WHERE trip_id = @TripId ORDER BY seat_no";
    private const string SnapshotCountSql = "SELECT count(*) FROM trip_snapshots.trip_snapshots WHERE trip_id = @TripId";
    private const string CancelledMarkSql = """
        INSERT INTO trip_snapshots.trip_snapshots (trip_id, is_cancelled, cancellation_reason, cancelled_at)
        VALUES (@TripId, true, 'Bus breakdown', now())
        """;

    private static readonly DateTimeOffset _departure = new DateTimeOffset(2026, 10, 26, 15, 30, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public CreateTripSnapshotTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_NewTrip_CreatesTheSnapshotWithAllItsSeats()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        CreateTripSnapshotHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Scheduled(tripId, ScheduledData(tripId)), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.Equal("OP-1", snapshot.OperatorId);
        Assert.Equal("Sharma Travels", snapshot.OperatorName);
        Assert.Equal("ops@sharma.example", snapshot.OperatorEmail);
        Assert.Equal("Mumbai", snapshot.SourceCityName);
        Assert.Equal("Pune", snapshot.DestinationCityName);
        Assert.Equal("Volvo 9600", snapshot.BusName);
        Assert.Equal("Sleeper", snapshot.BusType);
        Assert.True(snapshot.IsAc);
        Assert.False(snapshot.IsCancelled);
        Assert.Equal("2026-10-26", snapshot.ServiceDate);
        Assert.Equal(_departure.UtcDateTime, snapshot.DepartureAt.ToUniversalTime());
        Assert.Equal(_departure.AddHours(3.5).UtcDateTime, snapshot.ArrivalAt.ToUniversalTime());
        IReadOnlyList<SeatRow> seats = await TestQueries.QueryAsync<SeatRow>(_database, SeatsSql, new { TripId = tripId });
        Assert.Equal(3, seats.Count);
        Assert.Contains(seats, seat => seat.SeatNo == "L1" && seat.SeatClass == "Lower berth" && seat.Fare == 1100);
    }

    [Fact]
    public async Task HandleAsync_TripAlreadyKnown_LeavesTheSnapshotAndItsFaresUnchanged()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        CreateTripSnapshotHandler handler = CreateHandler();
        await handler.HandleAsync(Scheduled(tripId, ScheduledData(tripId)), CancellationToken.None);
        await _database.SeedAsync(
            "UPDATE trip_snapshots.trip_snapshot_seats SET fare = 1200 WHERE trip_id = @TripId AND seat_no = 'L1'", new { TripId = tripId });
        Dictionary<string, object?> another = ScheduledData(tripId);
        another["busName"] = "Other bus";

        // Act
        EventOutcome repeat = await handler.HandleAsync(Scheduled(tripId, ScheduledData(tripId)), CancellationToken.None);
        EventOutcome different = await handler.HandleAsync(Scheduled(tripId, another), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, repeat.Kind);
        Assert.Equal(EventOutcomeKind.Applied, different.Kind);
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.Equal("Volvo 9600", snapshot.BusName);
        IReadOnlyList<SeatRow> seats = await TestQueries.QueryAsync<SeatRow>(_database, SeatsSql, new { TripId = tripId });
        Assert.Equal(3, seats.Count);
        Assert.Contains(seats, seat => seat.SeatNo == "L1" && seat.Fare == 1200);
    }

    [Fact]
    public async Task HandleAsync_TripRememberedAsCancelled_FillsInTheDetailsAndStaysCancelled()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(CancelledMarkSql, new { TripId = tripId });
        CreateTripSnapshotHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Scheduled(tripId, ScheduledData(tripId)), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.True(snapshot.IsCancelled);
        Assert.True(snapshot.HasCancelledAt);
        Assert.Equal("Sharma Travels", snapshot.OperatorName);
        IReadOnlyList<SeatRow> seats = await TestQueries.QueryAsync<SeatRow>(_database, SeatsSql, new { TripId = tripId });
        Assert.Equal(3, seats.Count);
    }

    [Theory]
    [InlineData("tripId")]
    [InlineData("operatorId")]
    [InlineData("departureUtc")]
    [InlineData("seats")]
    [InlineData("source")]
    [InlineData("busType")]
    public async Task HandleAsync_DataMissingAMember_IsRejectedAsInvalidAndCreatesNothing(string missing)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = ScheduledData(tripId);
        data.Remove(missing);

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Scheduled(tripId, data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
        Assert.Contains("E-9", outcome.Problem);
        Assert.Equal(0, await CountSnapshotsAsync(tripId));
    }

    [Fact]
    public async Task HandleAsync_TwoSeatsWithTheSameLabel_IsRejectedAsInvalidAndCreatesNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = ScheduledData(tripId);
        data["seats"] = new[] { Seat("L1", "Lower berth", 1100), Seat("L1", "Upper berth", 900) };

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Scheduled(tripId, data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(0, await CountSnapshotsAsync(tripId));
    }

    [Fact]
    public async Task HandleAsync_SeatWithoutAClassOrFare_IsRejectedAsInvalidAndCreatesNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = ScheduledData(tripId);
        data["seats"] = new object[] { Seat("L1", "Lower berth", 1100), new { seatNo = "L2", fare = 900 }, new { seatNo = "L3", @class = "Upper berth" } };

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Scheduled(tripId, data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(0, await CountSnapshotsAsync(tripId));
    }

    [Fact]
    public async Task HandleAsync_CitiesNamedWithTheirNameMember_ReadsTheNames()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        Dictionary<string, object?> data = ScheduledData(tripId);
        data["source"] = new { cityId = Guid.NewGuid(), name = "Delhi" };

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Scheduled(tripId, data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.Equal("Delhi", snapshot.SourceCityName);
    }

    private static CreateTripSnapshotHandler CreateHandler(IWriterConnectionFactory writerFactory)
    {
        return new CreateTripSnapshotHandler(writerFactory, NullLogger<CreateTripSnapshotHandler>.Instance);
    }

    private CreateTripSnapshotHandler CreateHandler()
    {
        return CreateHandler(TestWriters.CreateWriter(_database));
    }

    private async Task<long> CountSnapshotsAsync(Guid tripId)
    {
        return await TestQueries.QuerySingleAsync<long>(_database, SnapshotCountSql, new { TripId = tripId });
    }

    private static object Seat(string seatNo, string seatClass, int fare)
    {
        return new { seatNo, @class = seatClass, fare, deck = "Lower", row = 1, col = 1 };
    }

    private static Dictionary<string, object?> ScheduledData(Guid tripId)
    {
        return new Dictionary<string, object?>
        {
            ["tripId"] = tripId,
            ["operatorId"] = "OP-1",
            ["operatorName"] = "Sharma Travels",
            ["operatorEmail"] = "ops@sharma.example",
            ["busName"] = "Volvo 9600",
            ["busType"] = "Sleeper",
            ["isAc"] = true,
            ["rating"] = 4.5,
            ["source"] = new { cityId = Guid.NewGuid(), cityName = "Mumbai" },
            ["destination"] = new { cityId = Guid.NewGuid(), cityName = "Pune" },
            ["serviceDate"] = "2026-10-26",
            ["departureUtc"] = "2026-10-26T15:30:00Z",
            ["arrivalUtc"] = "2026-10-26T19:00:00Z",
            ["seats"] = new[] { Seat("L1", "Lower berth", 1100), Seat("L2", "Lower berth", 1100), Seat("U1", "Upper berth", 900) },
        };
    }

    private static ReceivedEvent Scheduled(Guid tripId, Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = "E-9",
            EventType = "TripScheduled",
            OccurredAt = _departure.AddDays(-5),
            CorrelationId = "corr-" + tripId,
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    private sealed class SnapshotRow
    {
        public string OperatorId { get; init; } = string.Empty;

        public string OperatorName { get; init; } = string.Empty;

        public string OperatorEmail { get; init; } = string.Empty;

        public string SourceCityName { get; init; } = string.Empty;

        public string DestinationCityName { get; init; } = string.Empty;

        public string BusName { get; init; } = string.Empty;

        public string BusType { get; init; } = string.Empty;

        public bool IsAc { get; init; }

        public bool IsCancelled { get; init; }

        public bool HasCancelledAt { get; init; }

        public string ServiceDate { get; init; } = string.Empty;

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }
    }

    private sealed class SeatRow
    {
        public string SeatNo { get; init; } = string.Empty;

        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
    }
}
