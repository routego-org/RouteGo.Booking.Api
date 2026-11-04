using RouteGo.Booking.Api.Common.Problems;

namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

[Collection(DatabaseCollection.Name)]
public sealed class CreateBookingTests
{
    private const string InsertEarlyCancellationSql = """
        INSERT INTO trip_snapshots.trip_snapshots (trip_id, is_cancelled, cancellation_reason, cancelled_at)
        VALUES (@TripId, true, 'Early', now())
        """;

    private const string SetFareSql = """
        UPDATE trip_snapshots.trip_snapshot_seats SET fare = @Fare WHERE trip_id = @TripId AND seat_no = @SeatNo
        """;

    private readonly DatabaseFixture _database;

    public CreateBookingTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_TwoFreeSeats_CreatesPaymentPendingBookingWithHoldAndCheckout()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 1600, "3A", "3B");

        // Assert
        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
        Booking booking = result.Response!;
        Assert.Equal(BookingStatus.PaymentPending, booking.Status);
        Assert.Equal(1600, booking.TotalAmount);
        Assert.Equal("INR", booking.Currency);
        Assert.Equal(CreateBookingScenario.Now.AddSeconds(600), booking.HoldExpiresAt);
        Assert.Equal(FakePaymentClient.DefaultCheckoutUrl, booking.CheckoutUrl);
        Assert.Equal("Priya Shah", booking.ContactName);
        Assert.Equal(CreateBookingScenario.CustomerEmail, booking.ContactEmail);
        Assert.Equal(new[] { 800L, 800L }, booking.Seats.Select(seat => seat.Fare).ToArray());
        Assert.Equal("Passenger 3A", booking.Seats[0].PassengerName);
        Assert.Equal(PassengerGender.Female, booking.Seats[0].PassengerGender);
        Assert.Equal("Mumbai", booking.Trip.SourceCityName);
        Assert.Equal(BusType.Sleeper, booking.Trip.BusType);
        Assert.Equal(0, booking.SeatChangeCount);
        Assert.Null(booking.Cancellation);
        Assert.Null(booking.Refund);
        Assert.Null(booking.FailureReason);
    }

    [Fact]
    public async Task HandleAsync_TwoFreeSeats_AsksSeatInventoryAndPaymentWithTheTokenIdentityAndRecordsNoEvent()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 1600, "3A", "3B");

        // Assert
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal(result.Response!.BookingId, row.BookingId);
        Assert.Equal("C-1", row.CustomerId);
        Assert.Equal("PaymentPending", row.Status);
        Assert.Equal(FakePaymentClient.DefaultCheckoutUrl, row.CheckoutUrl);
        FakeHoldCall hold = Assert.Single(scenario.SeatInventory.Holds);
        Assert.Equal(row.BookingId, hold.BookingId);
        Assert.Equal(new[] { "3A", "3B" }, hold.SeatNos.ToArray());
        Assert.Equal("corr-create", hold.CorrelationId);
        FakePaymentStart start = Assert.Single(scenario.Payment.Starts);
        Assert.Equal(new FakePaymentStart(row.BookingId, 1600, CreateBookingScenario.CustomerEmail), start);
        Assert.Empty(await scenario.GetEventsAsync());
    }

    [Fact]
    public async Task HandleAsync_HoldLengthSetTo60Seconds_AsksForSixtySecondsAndStoresSeatInventoryExpiry()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);
        scenario.Settings.Current = scenario.Settings.Current with { HoldSeconds = 60 };
        scenario.SeatInventory.HoldResult = new HoldSeatsResult
        {
            Outcome = HoldSeatsOutcome.Held,
            HoldExpiresAt = CreateBookingScenario.Now.AddSeconds(60),
        };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(60, Assert.Single(scenario.SeatInventory.Holds).HoldSeconds);
        Assert.Equal(CreateBookingScenario.Now.AddSeconds(60), result.Response!.HoldExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_FareRaisedSinceCustomerLooked_RefusesWithCurrentTotalThenAcceptsTheNewTotal()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        await _database.SeedAsync(SetFareSql, new { trip.TripId, SeatNo = "3A", Fare = 900 });
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult refused = await scenario.BookAsync(trip.TripId, 800, "3A");
        int holdsAfterRefusal = scenario.SeatInventory.Holds.Count;
        int startsAfterRefusal = scenario.Payment.Starts.Count;
        int bookingsAfterRefusal = (await scenario.GetBookingsAsync()).Count;
        CreateBookingResult accepted = await scenario.BookAsync(trip.TripId, 900, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.PriceChanged, refused.Outcome);
        Assert.Equal(900, refused.CurrentTotal);
        SeatFareProblemItem fare = Assert.Single(refused.SeatFares);
        Assert.Equal(new SeatFareProblemItem("3A", 900), fare);
        Assert.Equal(0, holdsAfterRefusal);
        Assert.Equal(0, startsAfterRefusal);
        Assert.Equal(0, bookingsAfterRefusal);
        Assert.Equal(CreateBookingOutcome.Created, accepted.Outcome);
    }

    [Fact]
    public async Task HandleAsync_ExistingPaymentPendingBooking_IsRefusedNamingItAndItsHoldExpiry()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        DateTimeOffset holdExpiresAt = CreateBookingScenario.Now.AddMinutes(5);
        var existing = new BookingSeedSpec { TripId = trip.TripId, HoldExpiresAt = holdExpiresAt };
        await BookingSeed.SeedBookingAsync(_database, existing);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 600, "4C");

        // Assert
        Assert.Equal(CreateBookingOutcome.UnpaidBookingExists, result.Outcome);
        Assert.Equal(existing.BookingId, result.ExistingBookingId);
        Assert.Equal(holdExpiresAt, result.HoldExpiresAt);
        Assert.Empty(scenario.SeatInventory.Holds);
        Assert.Single(await scenario.GetBookingsAsync());
    }

    [Fact]
    public async Task HandleAsync_ExistingBookingAlreadyConfirmed_AllowsANewBooking()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var confirmed = new BookingSeedSpec
        {
            TripId = trip.TripId,
            Status = BookingStatus.Confirmed,
            ConfirmedAt = CreateBookingScenario.Now.AddDays(-1),
        };
        await BookingSeed.SeedBookingAsync(_database, confirmed);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 600, "4C");

        // Assert
        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_TwoIdenticalRequestsAtOnce_CreatesOneBookingAndRefusesTheOther()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        Task<CreateBookingResult> first = scenario.BookAsync(trip.TripId, 800, "3A");
        Task<CreateBookingResult> second = scenario.BookAsync(trip.TripId, 800, "3A");
        CreateBookingResult[] results = await Task.WhenAll(first, second);

        // Assert
        Assert.Single(results, result => result.Outcome == CreateBookingOutcome.Created);
        CreateBookingResult refused = Assert.Single(results, result => result.Outcome == CreateBookingOutcome.UnpaidBookingExists);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal(row.BookingId, refused.ExistingBookingId);
        Assert.Single(scenario.SeatInventory.Holds);
    }

    [Fact]
    public async Task HandleAsync_TwentyNineMinutesBeforeDeparture_IsRefusedBookingClosedWithTheCutoff()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        DateTimeOffset departureAt = CreateBookingScenario.Now.AddMinutes(29);
        var trip = new TripSeed { DepartureAt = departureAt };
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.BookingClosed, result.Outcome);
        Assert.Equal(departureAt, result.DepartureAt);
        Assert.Equal(30, result.CutoffMinutes);
        Assert.Empty(scenario.SeatInventory.Holds);
    }

    [Fact]
    public async Task HandleAsync_TripExactlyAtTheCutoff_IsStillAccepted()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { DepartureAt = CreateBookingScenario.Now.AddMinutes(30) };
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_TripCancelledAndAlsoClosed_IsRefusedTripCancelledAndHoldsNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { IsCancelled = true, DepartureAt = CreateBookingScenario.Now.AddMinutes(10) };
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.TripCancelled, result.Outcome);
        Assert.Empty(scenario.SeatInventory.Holds);
        Assert.Empty(await scenario.GetBookingsAsync());
    }

    [Fact]
    public async Task HandleAsync_TripWithNoSnapshot_IsRefusedNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(Guid.NewGuid(), 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.TripNotFound, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_TripKnownOnlyFromAnEarlyCancellation_IsRefusedNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(InsertEarlyCancellationSql, new { TripId = tripId });
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(tripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.TripNotFound, result.Outcome);
        Assert.Empty(scenario.SeatInventory.Holds);
    }

    [Fact]
    public async Task HandleAsync_SeatTheTripDoesNotHave_IsRefusedNamingTheUnknownSeatBeforeThePriceCheck()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 1, "3A", "Z99");

        // Assert
        Assert.Equal(CreateBookingOutcome.UnknownSeats, result.Outcome);
        Assert.Equal(new[] { "Z99" }, result.SeatNos.ToArray());
        Assert.Empty(scenario.SeatInventory.Holds);
        Assert.Empty(await scenario.GetBookingsAsync());
    }
}
