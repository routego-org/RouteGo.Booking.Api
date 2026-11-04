using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.TripSnapshots.UpdateTripFares;

[Collection(DatabaseCollection.Name)]
public sealed class UpdateTripFaresTests
{
    private const string SeatFaresSql = "SELECT seat_no, fare FROM trip_snapshots.trip_snapshot_seats WHERE trip_id = @TripId ORDER BY seat_no";
    private const string BookedFaresSql = "SELECT seat_no, fare FROM bookings.booking_seats WHERE booking_id = @BookingId ORDER BY seat_no";
    private const string BookingTotalSql = "SELECT total_amount FROM bookings.bookings WHERE booking_id = @BookingId";

    private static readonly DateTimeOffset _ten = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public UpdateTripFaresTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_FareChange_GivesNewBookingsTheNewFaresAndKeepsBookedFares()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        var booking = new BookingSeedSpec
        {
            TripId = tripId,
            Passengers = new List<SeedPassenger> { new SeedPassenger("3A", "Priya Shah", 800) },
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        UpdateTripFaresHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(FareChanged("F-1", tripId, _ten, 900), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        IReadOnlyList<SeatFare> seats = await TestQueries.QueryAsync<SeatFare>(_database, SeatFaresSql, new { TripId = tripId });
        Assert.Contains(seats, seat => seat.SeatNo == "3A" && seat.Fare == 900);
        Assert.Contains(seats, seat => seat.SeatNo == "3B" && seat.Fare == 900);
        Assert.Contains(seats, seat => seat.SeatNo == "4C" && seat.Fare == 600);
        IReadOnlyList<SeatFare> booked = await TestQueries.QueryAsync<SeatFare>(_database, BookedFaresSql, new { booking.BookingId });
        Assert.Equal(800, booked.Single().Fare);
        Assert.Equal(800, await TestQueries.QuerySingleAsync<long>(_database, BookingTotalSql, new { booking.BookingId }));
    }

    [Fact]
    public async Task HandleAsync_EarlierFareChangeArrivingLate_KeepsTheLaterFaresAndRepeatChangesNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        UpdateTripFaresHandler handler = CreateHandler();
        await handler.HandleAsync(FareChanged("F-2", tripId, _ten.AddMinutes(5), 950), CancellationToken.None);

        // Act
        EventOutcome late = await handler.HandleAsync(FareChanged("F-1", tripId, _ten, 700), CancellationToken.None);
        EventOutcome repeat = await handler.HandleAsync(FareChanged("F-2", tripId, _ten.AddMinutes(5), 950), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, late.Kind);
        Assert.Equal(EventOutcomeKind.Applied, repeat.Kind);
        IReadOnlyList<SeatFare> seats = await TestQueries.QueryAsync<SeatFare>(_database, SeatFaresSql, new { TripId = tripId });
        Assert.Contains(seats, seat => seat.SeatNo == "3A" && seat.Fare == 950);
    }

    [Fact]
    public async Task HandleAsync_FareChangeForAnUnknownTrip_IsPostponedAsTripUnknownThenAppliedOnceTheTripExists()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        UpdateTripFaresHandler handler = CreateHandler();
        ReceivedEvent receivedEvent = FareChanged("F-1", tripId, _ten, 900);

        // Act
        EventOutcome first = await handler.HandleAsync(receivedEvent, CancellationToken.None);
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        EventOutcome second = await handler.HandleAsync(receivedEvent, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, first.Kind);
        Assert.Equal("tripUnknown", first.Reason);
        Assert.Equal(EventOutcomeKind.Applied, second.Kind);
        IReadOnlyList<SeatFare> seats = await TestQueries.QueryAsync<SeatFare>(_database, SeatFaresSql, new { TripId = tripId });
        Assert.Contains(seats, seat => seat.SeatNo == "3A" && seat.Fare == 900);
    }

    [Fact]
    public async Task HandleAsync_FareChangeForATripKnownOnlyAsCancelled_IsPostponedAsTripUnknown()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(
            "INSERT INTO trip_snapshots.trip_snapshots (trip_id, is_cancelled, cancellation_reason, cancelled_at) VALUES (@TripId, true, 'x', now())",
            new { TripId = tripId });

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(FareChanged("F-1", tripId, _ten, 900), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, outcome.Kind);
        Assert.Equal("tripUnknown", outcome.Reason);
    }

    [Theory]
    [InlineData("tripId")]
    [InlineData("seats")]
    public async Task HandleAsync_DataMissingAMember_IsRejectedAsInvalidAndChangesNothing(string missing)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        Dictionary<string, object?> data = FareData(tripId, 900);
        data.Remove(missing);

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Event("F-9", _ten, data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
        Assert.Contains("F-9", outcome.Problem);
        IReadOnlyList<SeatFare> seats = await TestQueries.QueryAsync<SeatFare>(_database, SeatFaresSql, new { TripId = tripId });
        Assert.Contains(seats, seat => seat.SeatNo == "3A" && seat.Fare == 800);
    }

    private static UpdateTripFaresHandler CreateHandler(IWriterConnectionFactory writerFactory)
    {
        return new UpdateTripFaresHandler(writerFactory, NullLogger<UpdateTripFaresHandler>.Instance);
    }

    private UpdateTripFaresHandler CreateHandler()
    {
        return CreateHandler(TestWriters.CreateWriter(_database));
    }

    private static Dictionary<string, object?> FareData(Guid tripId, int sleeperFare)
    {
        return new Dictionary<string, object?>
        {
            ["tripId"] = tripId,
            ["classFares"] = new[] { new { @class = "Sleeper", fare = sleeperFare } },
            ["seats"] = new[]
            {
                new { seatNo = "3A", @class = "Sleeper", fare = sleeperFare },
                new { seatNo = "3B", @class = "Sleeper", fare = sleeperFare },
            },
        };
    }

    private static ReceivedEvent FareChanged(string eventId, Guid tripId, DateTimeOffset occurredAt, int sleeperFare)
    {
        return Event(eventId, occurredAt, FareData(tripId, sleeperFare));
    }

    private static ReceivedEvent Event(string eventId, DateTimeOffset occurredAt, Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = eventId,
            EventType = "FareChanged",
            OccurredAt = occurredAt,
            CorrelationId = "corr-fare",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    private sealed class SeatFare
    {
        public string SeatNo { get; init; } = string.Empty;

        public long Fare { get; init; }
    }
}
