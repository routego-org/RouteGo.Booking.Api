using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentSucceeded;

[Collection(DatabaseCollection.Name)]
public sealed class ApplyPaymentSucceededTests
{
    private const long Total = 1600;

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private const string BookingSql = "SELECT status, failure_reason, payment_id, confirmed_at FROM bookings.bookings WHERE booking_id = @BookingId";
    private const string RefundSql = "SELECT payment_id, reason, amount, refund_key, status FROM cancellations.refunds WHERE booking_id = @BookingId";
    private const string RefundCountSql = "SELECT count(*) FROM cancellations.refunds";
    private const string EventTypesSql = "SELECT event_type FROM bookings.booking_events ORDER BY recorded_at";
    private const string EventDataSql = "SELECT event_data FROM bookings.booking_events WHERE event_type = @EventType";
    private const string ExpireSql = "UPDATE bookings.bookings SET status = 'Expired' WHERE booking_id = @BookingId";

    private readonly DatabaseFixture _database;
    private readonly FakeSeatInventoryClient _seatInventory = new FakeSeatInventoryClient();

    public ApplyPaymentSucceededTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_SuccessForAPaymentPendingBooking_ConfirmsItAndRecordsBookingConfirmed()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.PaymentPending);
        var paymentId = Guid.NewGuid();
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, paymentId, "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(new[] { $"{trip.TripId}/{booking.BookingId}" }, _seatInventory.Confirms);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Confirmed", row.Status);
        Assert.Equal(paymentId, row.PaymentId);
        Assert.Equal(_now, UtcTimes.ToOffset(row.ConfirmedAt!.Value));
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "BookingConfirmed" }, types);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql));
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "BookingConfirmed" }))!;
        Assert.Contains("priya@example.com", data);
        Assert.Contains("Sharma Travels", data);
        Assert.Contains("3A", data);
    }

    [Fact]
    public async Task HandleAsync_SuccessForASeatsHeldBookingWithAmountAsDecimalNumber_ConfirmsIt()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.SeatsHeld);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600.0"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Confirmed", row.Status);
    }

    [Fact]
    public async Task HandleAsync_SameSuccessFiveTimesAndASecondSuccess_ConfirmsOnceWithoutARefund()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);
        ReceivedEvent success = Succeeded(booking.BookingId, Guid.NewGuid(), "1600");
        await handler.HandleAsync(success, CancellationToken.None);
        await handler.HandleAsync(success, CancellationToken.None);
        await handler.HandleAsync(success, CancellationToken.None);
        await handler.HandleAsync(success, CancellationToken.None);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Confirmed", row.Status);
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "BookingConfirmed" }, types);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql));
        Assert.Single(_seatInventory.Confirms);
    }

    [Fact]
    public async Task HandleAsync_SuccessForAnExpiredBooking_KeepsItExpiredAndRequestsALatePaymentRefund()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.Expired);
        var paymentId = Guid.NewGuid();
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, paymentId, "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Expired", row.Status);
        RefundRow refund = await TestQueries.QuerySingleAsync<RefundRow>(_database, RefundSql, new { booking.BookingId });
        Assert.Equal("LatePayment", refund.Reason);
        Assert.Equal(Total, refund.Amount);
        Assert.Equal(paymentId, refund.PaymentId);
        Assert.Equal("Requested", refund.Status);
        Assert.Equal($"{booking.BookingId}:payment", refund.RefundKey);
        Assert.Empty(_seatInventory.Confirms);
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "RefundRequested" }))!;
        Assert.Contains($"{booking.BookingId}:payment", data);
        Assert.Contains("LatePayment", data);
        Assert.Contains(paymentId.ToString(), data);
    }

    [Fact]
    public async Task HandleAsync_SuccessForAnAbandonedBooking_KeepsItFailedAndRequestsARefund()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        var spec = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Failed, FailureReason = FailureReason.Abandoned };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, spec);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(spec.BookingId, Guid.NewGuid(), "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { spec.BookingId });
        Assert.Equal("Failed", row.Status);
        Assert.Equal("Abandoned", row.FailureReason);
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "RefundRequested" }, types);
    }

    [Fact]
    public async Task HandleAsync_LateSuccessReportedTwice_RefundsOnceAndPublishesOnce()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.Expired);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);
        ReceivedEvent success = Succeeded(booking.BookingId, Guid.NewGuid(), "1600");
        await handler.HandleAsync(success, CancellationToken.None);

        // Act
        EventOutcome outcome = await handler.HandleAsync(success, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(RefundCountSql));
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "RefundRequested" }, types);
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryHoldsNoSeats_FailsTheBookingWithSeatsLostAndRefundsInFull()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        _seatInventory.ConfirmResult = new ConfirmSeatsResult(ConfirmSeatsOutcome.HoldNotActive);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Failed", row.Status);
        Assert.Equal("SeatsLost", row.FailureReason);
        RefundRow refund = await TestQueries.QuerySingleAsync<RefundRow>(_database, RefundSql, new { booking.BookingId });
        Assert.Equal("SeatsLost", refund.Reason);
        Assert.Equal(Total, refund.Amount);
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "BookingFailed", "RefundRequested" }, types);
        string failed = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "BookingFailed" }))!;
        Assert.Contains("SeatsLost", failed);
        Assert.Contains("priya@example.com", failed);
        Assert.Contains("Mumbai", failed);
        Assert.Contains("Pune", failed);
    }

    [Fact]
    public async Task HandleAsync_TripCancelledAtSeatInventory_FailsTheBookingWithTripCancelledAndRefundsInFull()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        _seatInventory.ConfirmResult = new ConfirmSeatsResult(ConfirmSeatsOutcome.TripCancelled);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Failed", row.Status);
        Assert.Equal("TripCancelled", row.FailureReason);
        RefundRow refund = await TestQueries.QuerySingleAsync<RefundRow>(_database, RefundSql, new { booking.BookingId });
        Assert.Equal("TripCancelled", refund.Reason);
        Assert.Equal(Total, refund.Amount);
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { EventType = "RefundRequested" }))!;
        Assert.Contains("TripCancelled", data);
    }

    [Theory]
    [InlineData(ConfirmSeatsOutcome.Unavailable)]
    [InlineData(ConfirmSeatsOutcome.TimedOut)]
    public async Task HandleAsync_SeatInventoryCannotAnswer_PostponesAndLeavesTheBookingWaiting(ConfirmSeatsOutcome outcomeOfConfirm)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        _seatInventory.ConfirmResult = new ConfirmSeatsResult(outcomeOfConfirm);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Postponed, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("PaymentPending", row.Status);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql));
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Empty(types);
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryIsBackOnTheRetry_ConfirmsTheBooking()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        _seatInventory.ConfirmResult = new ConfirmSeatsResult(ConfirmSeatsOutcome.Unavailable);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);
        ReceivedEvent success = Succeeded(booking.BookingId, Guid.NewGuid(), "1600");
        await handler.HandleAsync(success, CancellationToken.None);
        _seatInventory.ConfirmResult = new ConfirmSeatsResult(ConfirmSeatsOutcome.Confirmed);

        // Act
        EventOutcome outcome = await handler.HandleAsync(success, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Confirmed", row.Status);
    }

    [Fact]
    public async Task HandleAsync_BookingExpiresWhileSeatInventoryConfirms_KeepsItExpiredAndRefundsAsALatePayment()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        var racing = new ExpiringSeatInventory(_database, booking.BookingId);
        ApplyPaymentSucceededHandler handler = CreateHandler(racing);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Expired", row.Status);
        RefundRow refund = await TestQueries.QuerySingleAsync<RefundRow>(_database, RefundSql, new { booking.BookingId });
        Assert.Equal("LatePayment", refund.Reason);
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "RefundRequested" }, types);
    }

    [Fact]
    public async Task HandleAsync_SuccessForAnUnknownBooking_IsRejectedAsBookingUnknown()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(Guid.NewGuid(), Guid.NewGuid(), "1500"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("bookingUnknown", outcome.Reason);
        Assert.Empty(_seatInventory.Confirms);
    }

    [Fact]
    public async Task HandleAsync_AmountDifferentFromTheBookingsTotal_IsRejectedAndNothingChanges()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1500"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("amountMismatch", outcome.Reason);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("PaymentPending", row.Status);
        Assert.Empty(_seatInventory.Confirms);
    }

    [Fact]
    public async Task HandleAsync_SuccessWithoutAPaymentId_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);
        ReceivedEvent receivedEvent = Wrap($$"""{"bookingId":"{{booking.BookingId}}","amount":1600}""");

        // Act
        EventOutcome outcome = await handler.HandleAsync(receivedEvent, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_AmountThatIsNotWhole_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Succeeded(booking.BookingId, Guid.NewGuid(), "1600.5"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
    }

    [Fact]
    public void EventType_Always_IsPaymentSucceeded()
    {
        // Arrange
        ApplyPaymentSucceededHandler handler = CreateHandler(_seatInventory);

        // Act
        string eventType = handler.EventType;

        // Assert
        Assert.Equal("PaymentSucceeded", eventType);
    }

    private ApplyPaymentSucceededHandler CreateHandler(ISeatInventoryClient seatInventory)
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(_database);
        return new ApplyPaymentSucceededHandler(
            writerFactory, seatInventory, new FixedClock(_now), NullLogger<ApplyPaymentSucceededHandler>.Instance);
    }

    private async Task<BookingSeedSpec> SeedAsync(TripSeed trip, BookingStatus status)
    {
        var booking = new BookingSeedSpec { TripId = trip.TripId, Status = status, HoldExpiresAt = _now.AddMinutes(10) };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking;
    }

    private static ReceivedEvent Succeeded(Guid bookingId, Guid paymentId, string amountJson)
    {
        return Wrap($$"""{"bookingId":"{{bookingId}}","paymentId":"{{paymentId}}","amount":{{amountJson}},"currency":"INR"}""");
    }

    private static ReceivedEvent Wrap(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new ReceivedEvent
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = "PaymentSucceeded",
            OccurredAt = _now,
            CorrelationId = "corr-1",
            Data = document.RootElement.Clone(),
        };
    }

    private sealed class BookingRow
    {
        public string Status { get; init; } = string.Empty;

        public string? FailureReason { get; init; }

        public Guid? PaymentId { get; init; }

        public DateTime? ConfirmedAt { get; init; }
    }

    private sealed class RefundRow
    {
        public Guid PaymentId { get; init; }

        public string Reason { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string RefundKey { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }

    // Seat Inventory confirming the seats at the very moment the hold timer expires the booking.
    private sealed class ExpiringSeatInventory : ISeatInventoryClient
    {
        private readonly DatabaseFixture _database;
        private readonly Guid _bookingId;

        public ExpiringSeatInventory(DatabaseFixture database, Guid bookingId)
        {
            _database = database;
            _bookingId = bookingId;
        }

        public async Task<ConfirmSeatsResult> ConfirmSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            await _database.SeedAsync(ExpireSql, new { BookingId = _bookingId }, "test:expire");
            return new ConfirmSeatsResult(ConfirmSeatsOutcome.Confirmed);
        }

        public Task<HoldSeatsResult> HoldSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<string> seatNos, int holdSeconds, string correlationId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ReleaseSeatsResult> ReleaseSeatsAsync(Guid tripId, Guid bookingId, string correlationId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ChangeSeatsResult> ChangeSeatsAsync(
            Guid tripId, Guid bookingId, IReadOnlyList<SeatMove> moves, string correlationId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
