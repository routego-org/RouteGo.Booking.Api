using System.Text.Json;

namespace RouteGo.Booking.Api.Features.Bookings.ReleaseBooking;

[Collection(DatabaseCollection.Name)]
public sealed class ReleaseBookingTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 12, 33, 0, TimeSpan.Zero);

    private const string StateSql = "SELECT status || '/' || coalesce(failure_reason, '-') FROM bookings.bookings WHERE booking_id = @BookingId";

    private const string EventsSql = "SELECT event_type, event_data FROM bookings.booking_events ORDER BY recorded_at";

    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at)
        VALUES (@BookingId, gen_random_uuid(), 'LatePayment', 1600, @BookingId || ':payment', 'Requested', @Now)
        """;

    private readonly DatabaseFixture _database;
    private readonly FakeSeatInventoryClient _seatInventory = new FakeSeatInventoryClient();

    public ReleaseBookingTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Theory]
    [InlineData(BookingStatus.PaymentPending)]
    [InlineData(BookingStatus.SeatsHeld)]
    public async Task HandleAsync_UnpaidBooking_EndsItFailedAbandonedAndAnswersWithIt(BookingStatus status)
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(status);

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.Released, result.Outcome);
        Booking response = result.Response!;
        Assert.Equal(BookingStatus.Failed, response.Status);
        Assert.Equal(FailureReason.Abandoned, response.FailureReason);
        Assert.Null(response.HoldExpiresAt);
        Assert.Null(response.CheckoutUrl);
        Assert.Equal(1600, response.TotalAmount);
        Assert.Equal(2, response.Seats.Count);
        Assert.Equal("Failed/Abandoned", await StateAsync(booking.BookingId));
    }

    [Fact]
    public async Task HandleAsync_UnpaidBooking_ReleasesTheHoldAndRecordsBookingFailedWithContactAndTrip()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);

        // Act
        await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        Assert.Equal($"{booking.TripId}/{booking.BookingId}", Assert.Single(_seatInventory.Releases));
        EventRow recorded = Assert.Single(await EventsAsync());
        Assert.Equal("BookingFailed", recorded.EventType);
        using var data = JsonDocument.Parse(recorded.EventData);
        JsonElement root = data.RootElement;
        Assert.Equal(booking.BookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal(booking.TripId, root.GetProperty("tripId").GetGuid());
        Assert.Equal("3A,3B", string.Join(',', root.GetProperty("seatNos").EnumerateArray().Select(seat => seat.GetString())));
        Assert.Equal("Abandoned", root.GetProperty("reason").GetString());
        Assert.Equal("Priya Shah", root.GetProperty("contact").GetProperty("name").GetString());
        Assert.Equal("priya@example.com", root.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal("Mumbai", root.GetProperty("trip").GetProperty("source").GetString());
        Assert.Equal("Pune", root.GetProperty("trip").GetProperty("destination").GetString());
        Assert.True(root.GetProperty("trip").TryGetProperty("departureUtc", out _), "No departureUtc.");
    }

    [Fact]
    public async Task HandleAsync_UnpaidBooking_AsksSeatInventoryToReleaseOnlyAfterTheAbandonIsCommitted()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        var probe = new StatusProbeSeatInventory(_database);

        // Act
        await HandleAsync(probe, booking.BookingId, "C-1");

        // Assert
        Assert.Equal("Failed/Abandoned", probe.StatusAtRelease);
    }

    [Fact]
    public async Task HandleAsync_AbandonedTwice_AnswersTheSameAndReleasesAndRecordsNothingMore()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        ReleaseBookingResult first = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Act
        ReleaseBookingResult second = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.Released, second.Outcome);
        Assert.Equal(first.Response!.BookingId, second.Response!.BookingId);
        Assert.Equal(BookingStatus.Failed, second.Response.Status);
        Assert.Equal(FailureReason.Abandoned, second.Response.FailureReason);
        Assert.Single(_seatInventory.Releases);
        Assert.Single(await EventsAsync());
    }

    [Fact]
    public async Task HandleAsync_AbandonedBookingWithALatePaymentRefund_AnswersWithTheRefund()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        await HandleAsync(_seatInventory, booking.BookingId, "C-1");
        await _database.SeedAsync(InsertRefundSql, new { booking.BookingId, Now = _now.UtcDateTime });

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        BookingRefund refund = result.Response!.Refund!;
        Assert.Equal(RefundReason.LatePayment, refund.Reason);
        Assert.Equal(1600, refund.Amount);
        Assert.Equal(RefundStatus.Requested, refund.Status);
        Assert.Null(refund.CompletedAt);
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBooking_IsRefusedNotUnpaidNamingConfirmedAndChangesNothing()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.Confirmed, null, _now.AddMinutes(-1));

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.NotUnpaid, result.Outcome);
        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal("Confirmed/-", await StateAsync(booking.BookingId));
        Assert.Empty(_seatInventory.Releases);
        Assert.Empty(await EventsAsync());
    }

    [Theory]
    [InlineData(BookingStatus.Expired, null)]
    [InlineData(BookingStatus.Failed, FailureReason.PaymentDeclined)]
    public async Task HandleAsync_ExpiredOrOtherwiseFailedBooking_IsRefusedNotUnpaidNamingItsStatus(BookingStatus status, FailureReason? reason)
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(status, reason, null);

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.NotUnpaid, result.Outcome);
        Assert.Equal(status, result.Status);
        Assert.Empty(_seatInventory.Releases);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryUnreachable_StillEndsTheBookingAndRecordsTheEvent()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        _seatInventory.ReleaseResult = new ReleaseSeatsResult(ReleaseSeatsOutcome.Unavailable);

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, booking.BookingId, "C-1");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.Released, result.Outcome);
        Assert.Equal(FailureReason.Abandoned, result.Response!.FailureReason);
        Assert.Equal("Failed/Abandoned", await StateAsync(booking.BookingId));
        Assert.Equal("BookingFailed", Assert.Single(await EventsAsync()).EventType);
    }

    [Fact]
    public async Task HandleAsync_AnotherCustomersBooking_IsRefusedForbiddenAndChangesNothing()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, booking.BookingId, "C-2");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.Forbidden, result.Outcome);
        Assert.Equal("PaymentPending/-", await StateAsync(booking.BookingId));
        Assert.Empty(_seatInventory.Releases);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();

        // Act
        ReleaseBookingResult result = await HandleAsync(_seatInventory, Guid.NewGuid(), "C-1");

        // Assert
        Assert.Equal(ReleaseBookingOutcome.NotFound, result.Outcome);
    }

    private Task<ReleaseBookingResult> HandleAsync(ISeatInventoryClient seatInventory, Guid bookingId, string customerId)
    {
        var handler = new ReleaseBookingHandler(TestWriters.CreateWriter(_database), seatInventory, new FixedClock(_now));
        var caller = new Caller(customerId, "someone@example.com", "Customer", null);
        return handler.HandleAsync(new ReleaseTarget(bookingId, caller, "corr-release"), CancellationToken.None);
    }

    private Task<BookingSeedSpec> SeedAsync(BookingStatus status)
    {
        return SeedAsync(status, null, null);
    }

    private async Task<BookingSeedSpec> SeedAsync(BookingStatus status, FailureReason? reason, DateTimeOffset? confirmedAt)
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            Status = status,
            FailureReason = reason,
            ConfirmedAt = confirmedAt,
            HoldExpiresAt = _now.AddMinutes(7),
            CheckoutUrl = "http://localhost:5099/pay/test",
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking;
    }

    private Task<string> StateAsync(Guid bookingId)
    {
        return TestQueries.QuerySingleAsync<string>(_database, StateSql, new { BookingId = bookingId });
    }

    private Task<IReadOnlyList<EventRow>> EventsAsync()
    {
        return TestQueries.QueryAsync<EventRow>(_database, EventsSql);
    }

    private sealed class EventRow
    {
        public string EventType { get; init; } = string.Empty;

        public string EventData { get; init; } = string.Empty;
    }

    private sealed class StatusProbeSeatInventory : ISeatInventoryClient
    {
        private readonly DatabaseFixture _database;

        public StatusProbeSeatInventory(DatabaseFixture database)
        {
            _database = database;
        }

        public string? StatusAtRelease { get; private set; }

        public Task<HoldSeatsResult> HoldSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HoldSeatsResult { Outcome = HoldSeatsOutcome.Held });
        }

        public Task<ConfirmSeatsResult> ConfirmSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ConfirmSeatsResult(ConfirmSeatsOutcome.Confirmed));
        }

        public async Task<ReleaseSeatsResult> ReleaseSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            StatusAtRelease = await TestQueries.QuerySingleAsync<string>(_database, StateSql, new { BookingId = bookingId });
            return new ReleaseSeatsResult(ReleaseSeatsOutcome.Released);
        }

        public Task<ChangeSeatsResult> ChangeSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Changed });
        }
    }
}
