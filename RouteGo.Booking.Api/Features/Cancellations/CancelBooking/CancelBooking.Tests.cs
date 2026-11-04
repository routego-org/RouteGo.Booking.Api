using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Cancellations.CancelBooking;

[Collection(DatabaseCollection.Name)]
public sealed class CancelBookingTests
{
    // 2026-10-25 at 12:00 UTC.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _paymentId = Guid.Parse("0a0a0a0a-0000-4000-8000-0000000000a7");

    private const string CustomerId = "C-1";
    private const string CorrelationId = "cancel-booking-test";

    private const string StatusSql = "SELECT status FROM bookings.bookings WHERE booking_id = @BookingId";

    private const string CancellationSql = """
        SELECT kind || '/' || coalesce(reason, '-') || '/' || refund_percent || '/' || refund_amount || '/' || cancelled_by
        FROM cancellations.cancellations WHERE booking_id = @BookingId
        """;

    private const string RefundSql = """
        SELECT reason || '/' || amount || '/' || refund_key || '/' || status || '/' || payment_id
        FROM cancellations.refunds WHERE booking_id = @BookingId
        """;

    private const string RefundCountSql = "SELECT count(*) FROM cancellations.refunds WHERE booking_id = @BookingId";

    private const string EventTypesSql = """
        SELECT coalesce(string_agg(event_type, ',' ORDER BY recorded_at), '-')
        FROM bookings.booking_events WHERE booking_id = @BookingId
        """;

    private const string EventDataSql = """
        SELECT event_data FROM bookings.booking_events WHERE booking_id = @BookingId AND event_type = @EventType
        """;

    private const string InsertAdminCancellationSql = """
        UPDATE bookings.bookings SET status = 'Cancelled' WHERE booking_id = @BookingId;
        INSERT INTO cancellations.cancellations (booking_id, kind, reason, refund_percent, refund_amount, cancelled_by, cancelled_at)
        VALUES (@BookingId, 'AdminCancelled', 'Bus did not arrive at the stop', 100, 1099, 'A-1', @CancelledAt)
        """;

    private readonly DatabaseFixture _database;

    public CancelBookingTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBookingDepartingInThirtyHours_CancelsItWithNinetyPercentAndRecordsTheRefund()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.Cancelled, result.Outcome);
        Assert.Equal("Cancelled", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
        Assert.Equal("CustomerCancelled/-/90/989/C-1", await _database.QueryScalarAsync<string>(CancellationSql, new { BookingId = bookingId }));
        string expectedRefund = $"Cancellation/989/{bookingId}:cancel/Requested/{_paymentId}";
        Assert.Equal(expectedRefund, await _database.QueryScalarAsync<string>(RefundSql, new { BookingId = bookingId }));
        Cancellation cancellation = result.Response!;
        Assert.Equal(CancellationKind.CustomerCancelled, cancellation.Kind);
        Assert.Null(cancellation.Reason);
        Assert.Equal(90, cancellation.RefundPercent);
        Assert.Equal(989, cancellation.RefundAmount);
        Assert.Equal(RefundStatus.Requested, cancellation.RefundStatus);
        Assert.Equal(_now, cancellation.CancelledAt);
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBooking_RecordsCancelledRefundRequestedAndAuditEventsInOrder()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal("BookingCancelled,RefundRequested,AuditRecorded", await EventTypesAsync(bookingId));
        JsonElement cancelled = await EventDataAsync(bookingId, "BookingCancelled");
        Assert.Equal("CustomerCancelled", cancelled.GetProperty("kind").GetString());
        Assert.Equal(90, cancelled.GetProperty("refundPercent").GetInt32());
        Assert.Equal(989, cancelled.GetProperty("refundAmount").GetInt64());
        Assert.Equal("3A", cancelled.GetProperty("seatNos")[0].GetString());
        Assert.Equal("3B", cancelled.GetProperty("seatNos")[1].GetString());
        Assert.Equal("Mumbai", cancelled.GetProperty("trip").GetProperty("source").GetString());
        Assert.False(cancelled.TryGetProperty("reason", out JsonElement _));
        JsonElement refund = await EventDataAsync(bookingId, "RefundRequested");
        Assert.Equal(_paymentId, refund.GetProperty("paymentId").GetGuid());
        Assert.Equal(989, refund.GetProperty("amount").GetInt64());
        Assert.Equal("Cancellation", refund.GetProperty("reason").GetString());
        Assert.Equal($"{bookingId}:cancel", refund.GetProperty("idempotencyKey").GetString());
        JsonElement audit = await EventDataAsync(bookingId, "AuditRecorded");
        Assert.Equal(CustomerId, audit.GetProperty("actorId").GetString());
        Assert.Equal("BookingCancelled", audit.GetProperty("action").GetString());
        Assert.Equal(bookingId, audit.GetProperty("entityId").GetGuid());
        Assert.Equal(90, audit.GetProperty("details").GetProperty("refundPercent").GetInt32());
    }

    [Fact]
    public async Task HandleAsync_BookingDepartingInTwoHours_CancelsItWithNoRefundAndNoRefundEvent()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(2));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, result.Response!.RefundPercent);
        Assert.Equal(0, result.Response!.RefundAmount);
        Assert.Null(result.Response!.RefundStatus);
        Assert.Equal("Cancelled", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql, new { BookingId = bookingId }));
        Assert.Equal("BookingCancelled,AuditRecorded", await EventTypesAsync(bookingId));
        JsonElement cancelled = await EventDataAsync(bookingId, "BookingCancelled");
        Assert.Equal(0, cancelled.GetProperty("refundAmount").GetInt64());
    }

    [Fact]
    public async Task HandleAsync_TwentyThreeHoursFiftyNineMinutesBeforeDeparture_AppliesTheFiftyPercentTier()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        DateTimeOffset laterNow = _now.AddHours(6).AddMinutes(1);
        CancelBookingHandler handler = CreateHandler(laterNow, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(50, result.Response!.RefundPercent);
        Assert.Equal(549, result.Response!.RefundAmount);
        Assert.Equal("CustomerCancelled/-/50/549/C-1", await _database.QueryScalarAsync<string>(CancellationSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_CancellingAgain_ReturnsTheSameCancellationAndRecordsNothingNew()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());
        CancelBookingResult first = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);
        CancelBookingHandler laterHandler = CreateHandler(_now.AddHours(10), new FixedBookingSettings());

        // Act
        CancelBookingResult repeat = await laterHandler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.AlreadyCancelled, repeat.Outcome);
        Assert.Equal(first.Response, repeat.Response);
        Assert.Equal(90, repeat.Response!.RefundPercent);
        Assert.Equal(989, repeat.Response!.RefundAmount);
        Assert.Equal(RefundStatus.Requested, repeat.Response!.RefundStatus);
        Assert.Equal("BookingCancelled,RefundRequested,AuditRecorded", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_PaymentPendingBooking_IsRefusedNamingItsStatusAndNothingChanges()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.PaymentPending, TimeSpan.FromHours(30));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.BookingNotConfirmed, result.Outcome);
        Assert.Equal(BookingStatus.PaymentPending, result.BookingStatus);
        Assert.Equal("PaymentPending", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
        Assert.Equal("-", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_BookingCancelledByAnAdmin_IsRefusedNotConfirmedAndNothingChanges()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        await _database.SeedAsync(InsertAdminCancellationSql, new { BookingId = bookingId, CancelledAt = _now.UtcDateTime });
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.BookingNotConfirmed, result.Outcome);
        Assert.Equal(BookingStatus.Cancelled, result.BookingStatus);
        Assert.Equal("-", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_TripAlreadyDeparted_IsRefusedWithTheDepartureTimeAndStaysConfirmed()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-5));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.TripDeparted, result.Outcome);
        Assert.Equal(_now.AddMinutes(-5), result.DepartedAt);
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_AnotherCustomer_IsRefusedAndTheBookingStaysConfirmed()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(bookingId, "C-2"), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.NotTheCustomersBooking, result.Outcome);
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        CancelBookingHandler handler = CreateHandler(_now, new FixedBookingSettings());

        // Act
        CancelBookingResult result = await handler.HandleAsync(Target(Guid.NewGuid(), CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(CancelBookingOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_TiersChangedBetweenTwoCancellations_TheSecondUsesTheNewTiers()
    {
        // Arrange
        Guid firstBookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        var settings = new FixedBookingSettings();
        CancelBookingHandler handler = CreateHandler(_now, settings);
        CancelBookingResult before = await handler.HandleAsync(Target(firstBookingId, CustomerId), CancellationToken.None);
        Guid secondBookingId = await SeedAdditionalBookingAsync();
        settings.Current = BookingSettingsValues.CreateDefault() with
        {
            CancellationTiers = new List<RefundTier> { new RefundTier(24, 80), new RefundTier(0, 0) },
        };

        // Act
        CancelBookingResult after = await handler.HandleAsync(Target(secondBookingId, CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(90, before.Response!.RefundPercent);
        Assert.Equal(80, after.Response!.RefundPercent);
        Assert.Equal(879, after.Response!.RefundAmount);
    }

    [Fact]
    public async Task Cancel_WithoutAnAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/bookings/{bookingId}/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    public async Task Cancel_CallerThatIsNotACustomer_IsRefusedForbiddenAndNothingChanges(string role)
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task Cancel_AnotherCustomersBooking_IsRefusedForbiddenWithItsCode()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-2", "other@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Cancel_UnknownBooking_IsRefusedNotFoundWithItsCode()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", CustomerId, "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, Guid.NewGuid(), token);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Cancel_ConfirmedBooking_ReturnsTheCancellationAsTheContractShapesIt()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", CustomerId, "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(bookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal("customerCancelled", root.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        Assert.Equal(90, root.GetProperty("refundPercent").GetInt32());
        Assert.Equal(989, root.GetProperty("refundAmount").GetInt64());
        Assert.Equal("INR", root.GetProperty("currency").GetString());
        Assert.Equal(_now, root.GetProperty("cancelledAt").GetDateTimeOffset());
        Assert.Equal("requested", root.GetProperty("refundStatus").GetString());
    }

    [Fact]
    public async Task Cancel_RepeatThroughTheHost_ReturnsTheSameCancellationWithTheSameStatus()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", CustomerId, "priya@example.com");
        using HttpResponseMessage first = await SendAsync(client, bookingId, token);

        // Act
        using HttpResponseMessage repeat = await SendAsync(client, bookingId, token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
        Assert.Equal("BookingCancelled,RefundRequested,AuditRecorded", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task Cancel_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.PaymentPending, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", CustomerId, "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BOOKING_NOT_CONFIRMED", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("paymentPending", body.RootElement.GetProperty("bookingStatus").GetString());
    }

    [Fact]
    public async Task Cancel_DepartedTrip_IsRefusedWithItsCodeAndDepartureTime()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-5));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", CustomerId, "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TRIP_DEPARTED", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(_now.AddMinutes(-5), body.RootElement.GetProperty("departedAt").GetDateTimeOffset());
    }

    private static CancelBookingTarget Target(Guid bookingId, string accountId)
    {
        var caller = new Caller(accountId, "priya@example.com", "Customer", null);
        return new CancelBookingTarget(bookingId, caller, CorrelationId);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid bookingId, string token)
    {
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/bookings/{bookingId}/cancel", token);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private async Task<string> EventTypesAsync(Guid bookingId)
    {
        return (await _database.QueryScalarAsync<string>(EventTypesSql, new { BookingId = bookingId }))!;
    }

    private async Task<JsonElement> EventDataAsync(Guid bookingId, string eventType)
    {
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { BookingId = bookingId, EventType = eventType }))!;
        using var document = JsonDocument.Parse(data);
        return document.RootElement.Clone();
    }

    private CancelBookingHandler CreateHandler(DateTimeOffset now, FixedBookingSettings settings)
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(_database);
        var clock = new FixedClock(now);
        return new CancelBookingHandler(writerFactory, settings, clock, NullLogger<CancelBookingHandler>.Instance);
    }

    private ApiFactory CreateHost()
    {
        var clock = new FixedClock(_now);
        var seatInventory = new FakeSeatInventoryClient();
        var payment = new FakePaymentClient();
        var settings = new FixedBookingSettings();
        return BookingTestHost.For(_database, BookingTestHost.Doubles(clock, seatInventory, payment, settings));
    }

    // C-1's booking of 3A at 600 and 3B at 499 (total 1099), paid by payment a7, on a trip leaving the given time after now.
    private async Task<Guid> SeedAsync(BookingStatus status, TimeSpan untilDeparture)
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { DepartureAt = _now + untilDeparture };
        await BookingSeed.SeedTripAsync(_database, trip);
        return await SeedBookingOnAsync(trip, status);
    }

    // A second confirmed booking of C-1 on a new trip leaving 30 hours after now, total 1099.
    private async Task<Guid> SeedAdditionalBookingAsync()
    {
        var trip = new TripSeed { DepartureAt = _now.AddHours(30) };
        await BookingSeed.SeedTripAsync(_database, trip);
        return await SeedBookingOnAsync(trip, BookingStatus.Confirmed);
    }

    private async Task<Guid> SeedBookingOnAsync(TripSeed trip, BookingStatus status)
    {
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = CustomerId,
            Status = status,
            PaymentId = _paymentId,
            ConfirmedAt = _now.AddDays(-1),
            Passengers = new List<SeedPassenger>
            {
                new SeedPassenger("3A", "Priya Shah", 600),
                new SeedPassenger("3B", "Ravi Shah", 499),
            },
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }
}
