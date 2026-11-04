namespace RouteGo.Booking.Api.Features.Bookings.ListTripPassengers;

[Collection(DatabaseCollection.Name)]
public sealed class ListTripPassengersTests
{
    private const string MoveSeatSql = """
        UPDATE bookings.booking_seats SET seat_no = @To WHERE booking_id = @BookingId AND seat_no = @From
        """;

    private const string InsertBareSnapshotSql = """
        INSERT INTO trip_snapshots.trip_snapshots (trip_id) VALUES (@TripId)
        """;

    private static readonly DateTimeOffset _confirmedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Caller _operator = new Caller("OP-1", "owner@busco.in", "Operator", "OP-1");

    private readonly DatabaseFixture _database;

    public ListTripPassengersTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_TwoConfirmedBookings_ListsEverySeatInSeatOrderWithTheTripAndSeatsBooked()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        TripSeed trip = CreateTrip();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid first = await SeedAsync(trip, "C-1", BookingStatus.Confirmed, new SeedPassenger("3A", "Priya Shah", 800), new SeedPassenger("3B", "Ravi Shah", 800));
        Guid second = await SeedAsync(trip, "C-2", BookingStatus.Confirmed, new SeedPassenger("1C", "Sameer Rao", 600));

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(trip.TripId, _operator, CancellationToken.None);

        // Assert
        Assert.Equal(ListTripPassengersOutcome.Listed, result.Outcome);
        TripPassengerList list = result.Response!;
        Assert.Equal(new[] { "1C", "3A", "3B" }, list.Passengers.Select(passenger => passenger.SeatNo).ToArray());
        Assert.Equal(new[] { "Sameer Rao", "Priya Shah", "Ravi Shah" }, list.Passengers.Select(passenger => passenger.PassengerName).ToArray());
        Assert.Equal(new[] { second, first, first }, list.Passengers.Select(passenger => passenger.BookingId).ToArray());
        Assert.Equal(new[] { "Seater", "Sleeper", "Sleeper" }, list.Passengers.Select(passenger => passenger.SeatClass).ToArray());
        Assert.All(list.Passengers, passenger => Assert.Equal(30, passenger.PassengerAge));
        Assert.All(list.Passengers, passenger => Assert.Equal(PassengerGender.Female, passenger.PassengerGender));
        Assert.Equal(3, list.SeatsBooked);
        Assert.Equal(trip.TripId, list.Trip.TripId);
        Assert.Equal("Mumbai", list.Trip.SourceCityName);
        Assert.Equal("Pune", list.Trip.DestinationCityName);
        Assert.Equal(trip.DepartureAt, list.Trip.DepartureAt);
        Assert.Equal(BusType.Sleeper, list.Trip.BusType);
    }

    [Fact]
    public async Task HandleAsync_UnpaidCancelledExpiredAndFailedBookings_AreNotListed()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        TripSeed trip = CreateTrip();
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", BookingStatus.Confirmed, new SeedPassenger("3A", "Priya Shah", 800));
        await SeedAsync(trip, "C-2", BookingStatus.PaymentPending, new SeedPassenger("4C", "Pending Person", 600));
        await SeedAsync(trip, "C-3", BookingStatus.Cancelled, new SeedPassenger("5C", "Cancelled Person", 600));
        await SeedAsync(trip, "C-4", BookingStatus.Expired, new SeedPassenger("6A", "Expired Person", 800));
        await SeedAsync(trip, "C-5", BookingStatus.Failed, new SeedPassenger("6B", "Failed Person", 800), FailureReason.PaymentDeclined);

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(trip.TripId, _operator, CancellationToken.None);

        // Assert
        TripPassenger only = Assert.Single(result.Response!.Passengers);
        Assert.Equal("3A", only.SeatNo);
        Assert.Equal(1, result.Response.SeatsBooked);
    }

    [Fact]
    public async Task HandleAsync_BookingMovedToAnotherSeat_ListsThePassengerOnTheNewSeatOnly()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        TripSeed trip = CreateTrip();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid booking = await SeedAsync(trip, "C-1", BookingStatus.Confirmed, new SeedPassenger("4C", "Sameer Rao", 600));
        await _database.SeedAsync(MoveSeatSql, new { BookingId = booking, From = "4C", To = "5C" });

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(trip.TripId, _operator, CancellationToken.None);

        // Assert
        TripPassenger moved = Assert.Single(result.Response!.Passengers);
        Assert.Equal("5C", moved.SeatNo);
        Assert.Equal("Sameer Rao", moved.PassengerName);
    }

    [Fact]
    public async Task HandleAsync_TripWithNoConfirmedBooking_ReturnsAnEmptyListWithZeroSeatsBooked()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        TripSeed trip = CreateTrip();
        await BookingSeed.SeedTripAsync(_database, trip);

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(trip.TripId, _operator, CancellationToken.None);

        // Assert
        Assert.Equal(ListTripPassengersOutcome.Listed, result.Outcome);
        Assert.Empty(result.Response!.Passengers);
        Assert.Equal(0, result.Response.SeatsBooked);
    }

    [Fact]
    public async Task HandleAsync_CancelledTrip_IsStillReadableAndItsCancelledBookingsAreNotListed()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { IsCancelled = true };
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", BookingStatus.Cancelled, new SeedPassenger("3A", "Priya Shah", 800));

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(trip.TripId, _operator, CancellationToken.None);

        // Assert
        Assert.Equal(ListTripPassengersOutcome.Listed, result.Outcome);
        Assert.Empty(result.Response!.Passengers);
    }

    [Fact]
    public async Task HandleAsync_TripOfAnotherOperator_IsForbidden()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { OperatorId = "OP-2" };
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", BookingStatus.Confirmed, new SeedPassenger("3A", "Priya Shah", 800));

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(trip.TripId, _operator, CancellationToken.None);

        // Assert
        Assert.Equal(ListTripPassengersOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task HandleAsync_TripWithNoSnapshot_IsNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(Guid.NewGuid(), _operator, CancellationToken.None);

        // Assert
        Assert.Equal(ListTripPassengersOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_SnapshotWithoutTripDetails_IsNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(InsertBareSnapshotSql, new { TripId = tripId });

        // Act
        ListTripPassengersResult result = await CreateHandler().HandleAsync(tripId, _operator, CancellationToken.None);

        // Assert
        Assert.Equal(ListTripPassengersOutcome.NotFound, result.Outcome);
    }

    private static TripSeed CreateTrip()
    {
        return new TripSeed
        {
            Seats = new List<SeedSeat>
            {
                new SeedSeat("1C", "Seater", 600),
                new SeedSeat("3A", "Sleeper", 800),
                new SeedSeat("3B", "Sleeper", 800),
                new SeedSeat("4C", "Seater", 600),
                new SeedSeat("5C", "Seater", 600),
                new SeedSeat("6A", "Sleeper", 800),
                new SeedSeat("6B", "Sleeper", 800),
            },
        };
    }

    private ListTripPassengersHandler CreateHandler()
    {
        return new ListTripPassengersHandler(TestWriters.CreateReader(_database));
    }

    private async Task<Guid> SeedAsync(TripSeed trip, string customerId, BookingStatus status, params SeedPassenger[] passengers)
    {
        return await SeedAsync(trip, customerId, status, passengers, null);
    }

    private async Task<Guid> SeedAsync(TripSeed trip, string customerId, BookingStatus status, SeedPassenger passenger, FailureReason reason)
    {
        return await SeedAsync(trip, customerId, status, new[] { passenger }, reason);
    }

    private async Task<Guid> SeedAsync(
        TripSeed trip, string customerId, BookingStatus status, IReadOnlyList<SeedPassenger> passengers, FailureReason? reason)
    {
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = customerId,
            Status = status,
            Passengers = passengers,
            FailureReason = reason,
            ConfirmedAt = _confirmedAt,
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }
}
