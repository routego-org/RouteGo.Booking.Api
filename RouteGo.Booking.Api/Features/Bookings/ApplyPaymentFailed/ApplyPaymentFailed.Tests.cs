using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentFailed;

[Collection(DatabaseCollection.Name)]
public sealed class ApplyPaymentFailedTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private const string BookingSql = "SELECT status, failure_reason, payment_failure_detail FROM bookings.bookings WHERE booking_id = @BookingId";
    private const string EventCountSql = "SELECT count(*) FROM bookings.booking_events";
    private const string EventDataSql = "SELECT event_data FROM bookings.booking_events WHERE event_type = 'BookingFailed'";

    private readonly DatabaseFixture _database;

    public ApplyPaymentFailedTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_FailureForAPaymentPendingBooking_FailsItKeepingPaymentsReasonAndRecordsBookingFailed()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.PaymentPending);
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Failed(booking.BookingId, "Card declined"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Failed", row.Status);
        Assert.Equal("PaymentDeclined", row.FailureReason);
        Assert.Equal("Card declined", row.PaymentFailureDetail);
        string data = (await _database.QueryScalarAsync<string>(EventDataSql))!;
        Assert.Contains("PaymentDeclined", data);
        Assert.Contains(trip.TripId.ToString(), data);
        Assert.Contains("3A", data);
        Assert.Contains("priya@example.com", data);
        Assert.Contains("Mumbai", data);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql));
    }

    [Fact]
    public async Task HandleAsync_FailureForASeatsHeldBooking_FailsIt()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.SeatsHeld);
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Failed(booking.BookingId, "Card declined"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Failed", row.Status);
    }

    [Fact]
    public async Task HandleAsync_FailureForAConfirmedBooking_ChangesNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        var spec = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Confirmed, ConfirmedAt = _now.AddMinutes(-5) };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, spec);
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Failed(spec.BookingId, "Card declined"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { spec.BookingId });
        Assert.Equal("Confirmed", row.Status);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(EventCountSql));
    }

    [Fact]
    public async Task HandleAsync_FailureForAnExpiredBooking_ChangesNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.Expired);
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Failed(booking.BookingId, "Card declined"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Expired", row.Status);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(EventCountSql));
    }

    [Fact]
    public async Task HandleAsync_FailureDeliveredTwice_RecordsBookingFailedOnce()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        BookingSeedSpec booking = await SeedAsync(new TripSeed(), BookingStatus.PaymentPending);
        ApplyPaymentFailedHandler handler = CreateHandler();
        await handler.HandleAsync(Failed(booking.BookingId, "Card declined"), CancellationToken.None);

        // Act
        EventOutcome outcome = await handler.HandleAsync(Failed(booking.BookingId, "Card declined"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(1, await _database.QueryScalarAsync<int>(EventCountSql));
    }

    [Fact]
    public async Task HandleAsync_FailureForAnUnknownBooking_IsRejectedAsBookingUnknown()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Failed(Guid.NewGuid(), "Card declined"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("bookingUnknown", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_FailureWithoutABookingId_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Wrap("""{"reason":"Card declined"}"""), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
    }

    [Fact]
    public void EventType_Always_IsPaymentFailed()
    {
        // Arrange
        ApplyPaymentFailedHandler handler = CreateHandler();

        // Act
        string eventType = handler.EventType;

        // Assert
        Assert.Equal("PaymentFailed", eventType);
    }

    private ApplyPaymentFailedHandler CreateHandler()
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(_database);
        return new ApplyPaymentFailedHandler(writerFactory, new FixedClock(_now), NullLogger<ApplyPaymentFailedHandler>.Instance);
    }

    private async Task<BookingSeedSpec> SeedAsync(TripSeed trip, BookingStatus status)
    {
        var booking = new BookingSeedSpec { TripId = trip.TripId, Status = status, HoldExpiresAt = _now.AddMinutes(10) };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking;
    }

    private static ReceivedEvent Failed(Guid bookingId, string reason)
    {
        return Wrap($$"""{"bookingId":"{{bookingId}}","paymentId":"{{Guid.NewGuid()}}","reason":"{{reason}}"}""");
    }

    private static ReceivedEvent Wrap(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new ReceivedEvent
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = "PaymentFailed",
            OccurredAt = _now,
            CorrelationId = "corr-1",
            Data = document.RootElement.Clone(),
        };
    }

    private sealed class BookingRow
    {
        public string Status { get; init; } = string.Empty;

        public string? FailureReason { get; init; }

        public string? PaymentFailureDetail { get; init; }
    }
}
