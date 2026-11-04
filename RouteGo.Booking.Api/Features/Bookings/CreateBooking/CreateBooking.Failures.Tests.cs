using System.Text.Json;

namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

[Collection(DatabaseCollection.Name)]
public sealed class CreateBookingFailureTests
{
    private const string ExpireSql = "UPDATE bookings.bookings SET status = 'Expired' WHERE booking_id = @BookingId";

    private readonly DatabaseFixture _database;

    public CreateBookingFailureTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_SeatTakenByAnotherBooking_RefusesNamingTheSeatAndRecordsFailedSeatsUnavailable()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult
        {
            Outcome = HoldSeatsOutcome.SeatsUnavailable,
            NamedSeats = new[] { "3B" },
        };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 1600, "3A", "3B");

        // Assert
        Assert.Equal(CreateBookingOutcome.SeatsUnavailable, result.Outcome);
        Assert.Equal(new[] { "3B" }, result.SeatNos.ToArray());
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("Failed", row.Status);
        Assert.Equal("SeatsUnavailable", row.FailureReason);
        Assert.Empty(scenario.Payment.Starts);
        Assert.Empty(scenario.SeatInventory.Releases);
    }

    [Fact]
    public async Task HandleAsync_AfterASeatsUnavailableFailure_TheCustomerMayBookOtherSeatsAtOnce()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult { Outcome = HoldSeatsOutcome.SeatsUnavailable, NamedSeats = new[] { "3B" } };
        await scenario.BookAsync(trip.TripId, 1600, "3A", "3B");
        scenario.SeatInventory.HoldResult = new HoldSeatsResult
        {
            Outcome = HoldSeatsOutcome.Held,
            HoldExpiresAt = CreateBookingScenario.Now.AddMinutes(10),
        };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 600, "4C");

        // Assert
        Assert.Equal(CreateBookingOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryKnowsTheTripCancelled_RefusesTripCancelledAndRecordsFailedTripCancelled()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult { Outcome = HoldSeatsOutcome.TripCancelled };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.TripCancelled, result.Outcome);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("TripCancelled", row.FailureReason);
        RecordedEventRow recorded = Assert.Single(await scenario.GetEventsAsync());
        Assert.Equal("BookingFailed", recorded.EventType);
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryTimesOut_RefusesWithTimeoutReleasesTheHoldAndRecordsFailedInventoryUnavailable()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult { Outcome = HoldSeatsOutcome.TimedOut };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.UpstreamTimeout, result.Outcome);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("Failed", row.Status);
        Assert.Equal("InventoryUnavailable", row.FailureReason);
        Assert.Equal($"{trip.TripId}/{row.BookingId}", Assert.Single(scenario.SeatInventory.Releases));
        Assert.Equal("BookingFailed", Assert.Single(await scenario.GetEventsAsync()).EventType);
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryDown_RefusesUnavailableRetryableAndReleasesTheHold()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult { Outcome = HoldSeatsOutcome.Unavailable };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.UpstreamUnavailable, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Single(scenario.SeatInventory.Releases);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("InventoryUnavailable", row.FailureReason);
    }

    [Theory]
    [InlineData(HoldSeatsOutcome.UnknownSeats)]
    [InlineData(HoldSeatsOutcome.Refused)]
    public async Task HandleAsync_SeatInventoryRefusesForItsOwnReasons_FailsInventoryUnavailableNotRetryableAndReleases(HoldSeatsOutcome outcome)
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult { Outcome = outcome };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.UpstreamUnavailable, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Single(scenario.SeatInventory.Releases);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("InventoryUnavailable", row.FailureReason);
    }

    [Fact]
    public async Task HandleAsync_PaymentUnavailable_ReleasesTheHoldRecordsFailedPaymentUnavailableAndRefusesUnavailable()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.Payment.Result = new StartPaymentResult { Outcome = StartPaymentOutcome.Unavailable };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 1600, "3A", "3B");

        // Assert
        Assert.Equal(CreateBookingOutcome.UpstreamUnavailable, result.Outcome);
        Assert.True(result.IsRetryable);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("Failed", row.Status);
        Assert.Equal("PaymentUnavailable", row.FailureReason);
        Assert.Equal($"{trip.TripId}/{row.BookingId}", Assert.Single(scenario.SeatInventory.Releases));
        Assert.Equal("BookingFailed", Assert.Single(await scenario.GetEventsAsync()).EventType);
    }

    [Fact]
    public async Task HandleAsync_PaymentTimesOut_RefusesWithTimeoutAndReleasesTheHold()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.Payment.Result = new StartPaymentResult { Outcome = StartPaymentOutcome.TimedOut };

        // Act
        CreateBookingResult result = await scenario.BookAsync(trip.TripId, 800, "3A");

        // Assert
        Assert.Equal(CreateBookingOutcome.UpstreamTimeout, result.Outcome);
        Assert.Single(scenario.SeatInventory.Releases);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("PaymentUnavailable", row.FailureReason);
    }

    [Fact]
    public async Task HandleAsync_FailedBooking_RecordsBookingFailedWithBookingTripSeatsContactAndReason()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.Payment.Result = new StartPaymentResult { Outcome = StartPaymentOutcome.Unavailable };

        // Act
        await scenario.BookAsync(trip.TripId, 1600, "3A", "3B");

        // Assert
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        RecordedEventRow recorded = Assert.Single(await scenario.GetEventsAsync());
        Assert.Equal("BookingEvents", recorded.Stream);
        Assert.Equal("corr-create", recorded.CorrelationId);
        Assert.Null(recorded.SentAt);
        using var data = JsonDocument.Parse(recorded.EventData);
        JsonElement root = data.RootElement;
        Assert.Equal(row.BookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal(trip.TripId, root.GetProperty("tripId").GetGuid());
        Assert.Equal("3A,3B", string.Join(',', root.GetProperty("seatNos").EnumerateArray().Select(seat => seat.GetString())));
        Assert.Equal("Priya Shah", root.GetProperty("contact").GetProperty("name").GetString());
        Assert.Equal(CreateBookingScenario.CustomerEmail, root.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal("Mumbai", root.GetProperty("trip").GetProperty("source").GetString());
        Assert.Equal("Pune", root.GetProperty("trip").GetProperty("destination").GetString());
        Assert.Equal(trip.DepartureAt, root.GetProperty("trip").GetProperty("departureUtc").GetDateTimeOffset());
        Assert.Equal("PaymentUnavailable", root.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HandleAsync_BookingMovedWhileTheHoldWasInFlight_IsReportedStateChangedAndRecordsNoEvent()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        var moving = new ExpiringSeatInventory(_database, scenario.SeatInventory);
        CreateBookingHandler handler = scenario.CreateHandler(moving);
        var order = new BookingOrder(
            new Caller("C-1", CreateBookingScenario.CustomerEmail, "Customer", null),
            CreateBookingScenario.Request(trip.TripId, 800, "3A"),
            "corr-create");

        // Act
        CreateBookingResult result = await handler.HandleAsync(order, scenario.Settings.Current, CancellationToken.None);

        // Assert
        Assert.Equal(CreateBookingOutcome.StateChanged, result.Outcome);
        BookingStateRow row = Assert.Single(await scenario.GetBookingsAsync());
        Assert.Equal("Expired", row.Status);
        Assert.Empty(await scenario.GetEventsAsync());
        Assert.Empty(scenario.Payment.Starts);
    }

    private async Task<(CreateBookingScenario Scenario, TripSeed Trip)> SeedAsync()
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        return (new CreateBookingScenario(_database, CreateBookingScenario.Now), trip);
    }

    private sealed class ExpiringSeatInventory : ISeatInventoryClient
    {
        private readonly DatabaseFixture _database;
        private readonly FakeSeatInventoryClient _inner;

        public ExpiringSeatInventory(DatabaseFixture database, FakeSeatInventoryClient inner)
        {
            _database = database;
            _inner = inner;
        }

        public async Task<HoldSeatsResult> HoldSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken)
        {
            await _database.SeedAsync(ExpireSql, new { BookingId = bookingId });
            return await _inner.HoldSeatsAsync(tripId, bookingId, seatNos, holdSeconds, correlationId, cancellationToken);
        }

        public Task<ConfirmSeatsResult> ConfirmSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            return _inner.ConfirmSeatsAsync(tripId, bookingId, correlationId, cancellationToken);
        }

        public Task<ReleaseSeatsResult> ReleaseSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            return _inner.ReleaseSeatsAsync(tripId, bookingId, correlationId, cancellationToken);
        }

        public Task<ChangeSeatsResult> ChangeSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken)
        {
            return _inner.ChangeSeatsAsync(tripId, bookingId, moves, correlationId, cancellationToken);
        }
    }
}
