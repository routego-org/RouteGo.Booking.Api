using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Cancellations.CompleteRefund;

[Collection(DatabaseCollection.Name)]
public sealed class CompleteRefundTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private const string RefundStatusSql = "SELECT status FROM cancellations.refunds WHERE booking_id = @BookingId";
    private const string RefundCompletedAtSql = "SELECT completed_at FROM cancellations.refunds WHERE booking_id = @BookingId";
    private const string EventCountSql = "SELECT count(*) FROM bookings.booking_events";

    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at)
        VALUES (@BookingId, @PaymentId, @Reason, @Amount, @RefundKey, 'Requested', @RequestedAt)
        """;

    private const string CompleteRefundSql = """
        UPDATE cancellations.refunds SET status = 'Completed', completed_at = @CompletedAt WHERE booking_id = @BookingId
        """;

    private readonly DatabaseFixture _database;

    public CompleteRefundTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_CancellationRefundReported_CompletesItWithTheTimeBookingLearnedOfIt()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(bookingId, "989"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Completed", await _database.QueryScalarAsync<string>(RefundStatusSql, new { BookingId = bookingId }));
        DateTime? completedAt = await _database.QueryScalarAsync<DateTime?>(RefundCompletedAtSql, new { BookingId = bookingId });
        Assert.Equal(_now, UtcTimes.ToOffset(completedAt!.Value));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(EventCountSql));
    }

    [Fact]
    public async Task HandleAsync_LatePaymentRefundReported_CompletesItTheSameWay()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Expired, 1600, RefundReason.LatePayment);
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(bookingId, "1600"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Completed", await _database.QueryScalarAsync<string>(RefundStatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_AmountSentAsAWholeJsonNumberWithADecimalPoint_IsStillAccepted()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(bookingId, "989.0"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Completed", await _database.QueryScalarAsync<string>(RefundStatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_CompletionDeliveredAgain_ChangesNothingAndKeepsTheCompletionTime()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        await CreateHandler(_now).HandleAsync(Completed(bookingId, "989"), CancellationToken.None);
        CompleteRefundHandler later = CreateHandler(_now.AddHours(2));

        // Act
        EventOutcome outcome = await later.HandleAsync(Completed(bookingId, "989"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        DateTime? completedAt = await _database.QueryScalarAsync<DateTime?>(RefundCompletedAtSql, new { BookingId = bookingId });
        Assert.Equal(_now, UtcTimes.ToOffset(completedAt!.Value));
    }

    [Fact]
    public async Task HandleAsync_BookingWithNoRefund_IsRejectedAsRefundUnknown()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid tripId = await SeedTripAsync();
        var booking = new BookingSeedSpec { TripId = tripId, Status = BookingStatus.PaymentPending };
        await BookingSeed.SeedBookingAsync(_database, booking);
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(booking.BookingId, "900"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("refundUnknown", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsRejectedAsRefundUnknown()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(Guid.NewGuid(), "900"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("refundUnknown", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_AmountDifferentFromTheRefund_IsRejectedAndTheRefundStaysRequested()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(bookingId, "900"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("amountMismatch", outcome.Reason);
        Assert.Equal("Requested", await _database.QueryScalarAsync<string>(RefundStatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_AmountDifferentFromACompletedRefund_IsRejectedAsAMismatch()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        await _database.SeedAsync(CompleteRefundSql, new { BookingId = bookingId, CompletedAt = _now.UtcDateTime });
        CompleteRefundHandler handler = CreateHandler(_now.AddHours(1));

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(bookingId, "900"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("amountMismatch", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_EventWithoutAnAmount_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        CompleteRefundHandler handler = CreateHandler(_now);
        ReceivedEvent receivedEvent = Wrap($$"""{"bookingId":"{{bookingId}}"}""");

        // Act
        EventOutcome outcome = await handler.HandleAsync(receivedEvent, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
        Assert.Equal("Requested", await _database.QueryScalarAsync<string>(RefundStatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_AmountThatIsNotWhole_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        Guid bookingId = await SeedRefundAsync(BookingStatus.Cancelled, 989, RefundReason.Cancellation);
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Completed(bookingId, "989.5"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_DataThatIsNotAnObject_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Wrap("\"oops\""), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
    }

    [Fact]
    public void EventType_Always_IsRefundCompleted()
    {
        // Arrange
        CompleteRefundHandler handler = CreateHandler(_now);

        // Act
        string eventType = handler.EventType;

        // Assert
        Assert.Equal("RefundCompleted", eventType);
    }

    private CompleteRefundHandler CreateHandler(DateTimeOffset now)
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(_database);
        return new CompleteRefundHandler(writerFactory, new FixedClock(now), NullLogger<CompleteRefundHandler>.Instance);
    }

    private async Task<Guid> SeedTripAsync()
    {
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        return trip.TripId;
    }

    private async Task<Guid> SeedRefundAsync(BookingStatus status, long amount, RefundReason reason)
    {
        Guid tripId = await SeedTripAsync();
        var booking = new BookingSeedSpec { TripId = tripId, Status = status, ConfirmedAt = _now.AddDays(-1) };
        await BookingSeed.SeedBookingAsync(_database, booking);
        string suffix = ":payment";
        if (reason == RefundReason.Cancellation)
        {
            suffix = ":cancel";
        }

        var refund = new
        {
            booking.BookingId,
            PaymentId = Guid.NewGuid(),
            Reason = reason.ToString(),
            Amount = amount,
            RefundKey = booking.BookingId + suffix,
            RequestedAt = _now.AddHours(-1).UtcDateTime,
        };
        await _database.SeedAsync(InsertRefundSql, refund);
        return booking.BookingId;
    }

    private static ReceivedEvent Completed(Guid bookingId, string amountJson)
    {
        string json = $$"""{"bookingId":"{{bookingId}}","paymentId":"{{Guid.NewGuid()}}","amount":{{amountJson}},"currency":"INR","reason":"Cancellation"}""";
        return Wrap(json);
    }

    private static ReceivedEvent Wrap(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new ReceivedEvent
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = "RefundCompleted",
            OccurredAt = _now,
            CorrelationId = "corr-1",
            Data = document.RootElement.Clone(),
        };
    }
}
