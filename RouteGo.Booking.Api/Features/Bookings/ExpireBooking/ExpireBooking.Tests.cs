using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Bookings.ExpireBooking;

[Collection(DatabaseCollection.Name)]
public sealed class ExpireBookingTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero);

    private const string StatusSql = "SELECT status FROM bookings.bookings WHERE booking_id = @BookingId";
    private const string EventTypesSql = "SELECT event_type FROM bookings.booking_events ORDER BY recorded_at";
    private const string EventDataSql = "SELECT event_data FROM bookings.booking_events WHERE event_type = 'BookingExpired'";

    private readonly DatabaseFixture _database;

    public ExpireBookingTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_HoldExpiredForAPaymentPendingBooking_ExpiresItAndRecordsBookingExpired()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.PaymentPending);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, booking.BookingId, "HoldExpired"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Expired", await _database.QueryScalarAsync<string>(StatusSql, new { booking.BookingId }));
        IReadOnlyList<string> types = await TestQueries.QueryAsync<string>(_database, EventTypesSql);
        Assert.Equal(new[] { "BookingExpired" }, types);
        string data = (await _database.QueryScalarAsync<string>(EventDataSql))!;
        Assert.Contains(trip.TripId.ToString(), data);
        Assert.Contains("3A", data);
        Assert.Contains("3B", data);
        Assert.Contains("priya@example.com", data);
        Assert.Contains("Mumbai", data);
        Assert.Contains("Pune", data);
    }

    [Fact]
    public async Task HandleAsync_AdminReleasedForASeatsHeldBooking_ExpiresIt()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.SeatsHeld);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, booking.BookingId, "AdminReleased"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Expired", await _database.QueryScalarAsync<string>(StatusSql, new { booking.BookingId }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>("SELECT count(*) FROM bookings.booking_events"));
    }

    [Theory]
    [InlineData("HoldReleased")]
    [InlineData("BookingFailed")]
    [InlineData("BookingCancelled")]
    public async Task HandleAsync_ReleaseBookingCausedForAFailedBooking_ChangesNothing(string reason)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        var spec = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Failed, FailureReason = FailureReason.Abandoned };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, spec);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, spec.BookingId, reason), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Failed", await _database.QueryScalarAsync<string>(StatusSql, new { spec.BookingId }));
        Assert.Equal(0, await _database.QueryScalarAsync<int>("SELECT count(*) FROM bookings.booking_events"));
    }

    [Theory]
    [InlineData("HoldReleased")]
    [InlineData("BookingCancelled")]
    public async Task HandleAsync_ReleaseBookingCausedForAWaitingBooking_LeavesItWaiting(string reason)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.PaymentPending);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, booking.BookingId, reason), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("PaymentPending", await _database.QueryScalarAsync<string>(StatusSql, new { booking.BookingId }));
        Assert.Equal(0, await _database.QueryScalarAsync<int>("SELECT count(*) FROM bookings.booking_events"));
    }

    [Fact]
    public async Task HandleAsync_ReleaseForACancelledBooking_ChangesNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        var spec = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Cancelled, ConfirmedAt = _now.AddDays(-1) };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, spec);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, spec.BookingId, "BookingCancelled"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Cancelled", await _database.QueryScalarAsync<string>(StatusSql, new { spec.BookingId }));
    }

    [Fact]
    public async Task HandleAsync_HoldExpiredForAConfirmedBooking_LeavesItConfirmedAndRecordsNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        var spec = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Confirmed, ConfirmedAt = _now.AddMinutes(-5) };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, spec);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, spec.BookingId, "HoldExpired"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { spec.BookingId }));
        Assert.Equal(0, await _database.QueryScalarAsync<int>("SELECT count(*) FROM bookings.booking_events"));
    }

    [Fact]
    public async Task HandleAsync_SameReleaseTwice_RecordsBookingExpiredOnce()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.PaymentPending);
        ExpireBookingHandler handler = CreateHandler();
        ReceivedEvent release = Released(trip.TripId, booking.BookingId, "HoldExpired");
        await handler.HandleAsync(release, CancellationToken.None);

        // Act
        EventOutcome outcome = await handler.HandleAsync(release, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Expired", await _database.QueryScalarAsync<string>(StatusSql, new { booking.BookingId }));
        Assert.Equal(1, await _database.QueryScalarAsync<int>("SELECT count(*) FROM bookings.booking_events"));
    }

    [Fact]
    public async Task HandleAsync_ReleaseForAnUnknownBooking_IsRejectedAsBookingUnknown()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(Guid.NewGuid(), Guid.NewGuid(), "HoldExpired"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal("bookingUnknown", outcome.Reason);
    }

    [Fact]
    public async Task HandleAsync_ReleaseWithAnUnknownReason_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        BookingSeedSpec booking = await SeedAsync(trip, BookingStatus.PaymentPending);
        ExpireBookingHandler handler = CreateHandler();

        // Act
        EventOutcome outcome = await handler.HandleAsync(Released(trip.TripId, booking.BookingId, "Whim"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
        Assert.Equal("PaymentPending", await _database.QueryScalarAsync<string>(StatusSql, new { booking.BookingId }));
    }

    [Fact]
    public async Task HandleAsync_ReleaseWithoutABookingId_IsRejectedAsInvalidEventData()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        ExpireBookingHandler handler = CreateHandler();
        ReceivedEvent receivedEvent = Wrap($$"""{"tripId":"{{Guid.NewGuid()}}","seatNos":["3A"],"reason":"HoldExpired"}""");

        // Act
        EventOutcome outcome = await handler.HandleAsync(receivedEvent, CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
    }

    [Fact]
    public void EventType_Always_IsSeatReleased()
    {
        // Arrange
        ExpireBookingHandler handler = CreateHandler();

        // Act
        string eventType = handler.EventType;

        // Assert
        Assert.Equal("SeatReleased", eventType);
    }

    private ExpireBookingHandler CreateHandler()
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(_database);
        return new ExpireBookingHandler(writerFactory, new FixedClock(_now), NullLogger<ExpireBookingHandler>.Instance);
    }

    private async Task<BookingSeedSpec> SeedAsync(TripSeed trip, BookingStatus status)
    {
        var booking = new BookingSeedSpec { TripId = trip.TripId, Status = status, HoldExpiresAt = _now.AddMinutes(10) };
        await BookingSeed.SeedTripAsync(_database, trip);
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking;
    }

    private static ReceivedEvent Released(Guid tripId, Guid bookingId, string reason)
    {
        string json = $$"""{"tripId":"{{tripId}}","bookingId":"{{bookingId}}","seatNos":["3A","3B"],"reason":"{{reason}}","releasedTo":"Available"}""";
        return Wrap(json);
    }

    private static ReceivedEvent Wrap(string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new ReceivedEvent
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = "SeatReleased",
            OccurredAt = _now,
            CorrelationId = "corr-1",
            Data = document.RootElement.Clone(),
        };
    }
}
