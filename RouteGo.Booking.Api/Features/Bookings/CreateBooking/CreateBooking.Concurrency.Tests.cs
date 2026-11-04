namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

[Collection(DatabaseCollection.Name)]
public sealed class CreateBookingConcurrencyTests
{
    private const int Customers = 100;

    private readonly DatabaseFixture _database;

    public CreateBookingConcurrencyTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_HundredCustomersBookTheSameSeatAtOnce_ExactlyOneReachesPaymentPending()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var scenario = new CreateBookingScenario(_database, CreateBookingScenario.Now);
        var inventory = new OneWinnerSeatInventory(CreateBookingScenario.Now.AddMinutes(10));
        CreateBookingHandler handler = scenario.CreateHandler(inventory);

        // Act
        IEnumerable<Task<CreateBookingResult>> attempts = Enumerable.Range(0, Customers)
            .Select(number => BookAsync(handler, scenario, trip.TripId, number));
        CreateBookingResult[] results = await Task.WhenAll(attempts);

        // Assert
        CreateBookingResult winner = Assert.Single(results, result => result.Outcome == CreateBookingOutcome.Created);
        Assert.Equal(Customers - 1, results.Count(result => result.Outcome == CreateBookingOutcome.SeatsUnavailable));
        Assert.All(
            results.Where(result => result.Outcome == CreateBookingOutcome.SeatsUnavailable),
            result => Assert.Equal(new[] { "3A" }, result.SeatNos.ToArray()));
        IReadOnlyList<BookingStateRow> rows = await scenario.GetBookingsAsync();
        BookingStateRow paying = Assert.Single(rows, row => row.Status == "PaymentPending");
        Assert.Equal(winner.Response!.BookingId, paying.BookingId);
        Assert.Equal(Customers - 1, rows.Count(row => row.Status == "Failed" && row.FailureReason == "SeatsUnavailable"));
    }

    private static Task<CreateBookingResult> BookAsync(CreateBookingHandler handler, CreateBookingScenario scenario, Guid tripId, int number)
    {
        BookingRequest request = CreateBookingScenario.Request(tripId, 800, "3A");
        var caller = new Caller($"C-{number}", $"customer{number}@example.com", "Customer", null);
        var order = new BookingOrder(caller, request, $"corr-{number}");
        return handler.HandleAsync(order, scenario.Settings.Current, CancellationToken.None);
    }

    /// <summary>A Seat Inventory that holds the seat for the first booking that asks and refuses every other, atomically.</summary>
    private sealed class OneWinnerSeatInventory : ISeatInventoryClient
    {
        private readonly object _gate = new object();
        private readonly DateTimeOffset _holdExpiresAt;
        private Guid _winner = Guid.Empty;

        public OneWinnerSeatInventory(DateTimeOffset holdExpiresAt)
        {
            _holdExpiresAt = holdExpiresAt;
        }

        public Task<HoldSeatsResult> HoldSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_winner == Guid.Empty)
                {
                    _winner = bookingId;
                }

                if (_winner == bookingId)
                {
                    return Task.FromResult(new HoldSeatsResult { Outcome = HoldSeatsOutcome.Held, HoldExpiresAt = _holdExpiresAt });
                }

                return Task.FromResult(new HoldSeatsResult { Outcome = HoldSeatsOutcome.SeatsUnavailable, NamedSeats = seatNos.ToList() });
            }
        }

        public Task<ConfirmSeatsResult> ConfirmSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ConfirmSeatsResult(ConfirmSeatsOutcome.Confirmed));
        }

        public Task<ReleaseSeatsResult> ReleaseSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ReleaseSeatsResult(ReleaseSeatsOutcome.Released));
        }

        public Task<ChangeSeatsResult> ChangeSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Changed });
        }
    }
}
