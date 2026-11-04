using System.Text.Json;

namespace RouteGo.Booking.Api.Features.Cancellations.PreviewBookingCancellation;

[Collection(DatabaseCollection.Name)]
public sealed class PreviewBookingCancellationTests
{
    // 2026-10-25 at 12:00 UTC.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private const string CustomerId = "C-1";
    private const string CountRowsSql = """
        SELECT (SELECT count(*) FROM cancellations.cancellations)
             + (SELECT count(*) FROM cancellations.refunds)
             + (SELECT count(*) FROM bookings.booking_events)
        """;

    private const string StatusSql = "SELECT status FROM bookings.bookings WHERE booking_id = @BookingId";

    private readonly DatabaseFixture _database;

    public PreviewBookingCancellationTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBookingDepartingInThirtyHours_ShowsNinetyPercentAndTheAmountRoundedDown()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        PreviewBookingCancellationResult result = await handler.HandleAsync(bookingId, Customer(CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(PreviewBookingCancellationOutcome.Previewed, result.Outcome);
        CancellationPreview preview = result.Response!;
        Assert.Equal(bookingId, preview.BookingId);
        Assert.Equal(1099, preview.TotalAmount);
        Assert.Equal("INR", preview.Currency);
        Assert.Equal(90, preview.RefundPercent);
        Assert.Equal(989, preview.RefundAmount);
        Assert.Equal(_now.AddHours(30), preview.DepartureAt);
        Assert.Equal(_now, preview.CalculatedAt);
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBooking_ChangesNothing()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        long rowsBefore = await _database.QueryScalarAsync<long>(CountRowsSql);
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        await handler.HandleAsync(bookingId, Customer(CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(rowsBefore, await _database.QueryScalarAsync<long>(CountRowsSql));
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
    }

    [Theory]
    [InlineData(1440, 90, 989)]
    [InlineData(1441, 90, 989)]
    [InlineData(1439, 50, 549)]
    [InlineData(360, 50, 549)]
    [InlineData(359, 0, 0)]
    [InlineData(1, 0, 0)]
    public async Task HandleAsync_TimeLeftOnATierBoundary_AppliesTheHigherTier(int minutesLeft, int expectedPercent, long expectedAmount)
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(minutesLeft));
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        PreviewBookingCancellationResult result = await handler.HandleAsync(bookingId, Customer(CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(expectedPercent, result.Response!.RefundPercent);
        Assert.Equal(expectedAmount, result.Response!.RefundAmount);
    }

    [Theory]
    [InlineData(BookingStatus.PaymentPending)]
    [InlineData(BookingStatus.Failed)]
    [InlineData(BookingStatus.Expired)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task HandleAsync_BookingThatIsNotConfirmed_IsRefusedNamingItsStatus(BookingStatus status)
    {
        // Arrange
        Guid bookingId = await SeedAsync(status, TimeSpan.FromHours(30));
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        PreviewBookingCancellationResult result = await handler.HandleAsync(bookingId, Customer(CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(PreviewBookingCancellationOutcome.BookingNotConfirmed, result.Outcome);
        Assert.Equal(status, result.BookingStatus);
    }

    [Fact]
    public async Task HandleAsync_TripAlreadyDeparted_IsRefusedWithTheDepartureTime()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-5));
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        PreviewBookingCancellationResult result = await handler.HandleAsync(bookingId, Customer(CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(PreviewBookingCancellationOutcome.TripDeparted, result.Outcome);
        Assert.Equal(_now.AddMinutes(-5), result.DepartedAt);
    }

    [Fact]
    public async Task HandleAsync_AnotherCustomersBooking_IsRefusedAsNotTheirs()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        PreviewBookingCancellationResult result = await handler.HandleAsync(bookingId, Customer("C-2"), CancellationToken.None);

        // Assert
        Assert.Equal(PreviewBookingCancellationOutcome.NotTheCustomersBooking, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        PreviewBookingCancellationHandler handler = CreateHandler(_now);

        // Act
        PreviewBookingCancellationResult result = await handler.HandleAsync(Guid.NewGuid(), Customer(CustomerId), CancellationToken.None);

        // Assert
        Assert.Equal(PreviewBookingCancellationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Preview_WithoutAnAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30));
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/bookings/{bookingId}/cancellationPreview");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    public async Task Preview_CallerThatIsNotACustomer_IsRefusedForbidden(string role)
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
    }

    [Fact]
    public async Task Preview_AnotherCustomersBooking_IsRefusedForbiddenWithItsCode()
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
    public async Task Preview_UnknownBooking_IsRefusedNotFoundWithItsCode()
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
    public async Task Preview_ConfirmedBooking_ReturnsThePreviewAsTheContractShapesIt()
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
        Assert.Equal(1099, root.GetProperty("totalAmount").GetInt64());
        Assert.Equal("INR", root.GetProperty("currency").GetString());
        Assert.Equal(90, root.GetProperty("refundPercent").GetInt32());
        Assert.Equal(989, root.GetProperty("refundAmount").GetInt64());
        Assert.Equal(_now.AddHours(30), root.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal(_now, root.GetProperty("calculatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Preview_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus()
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
    public async Task Preview_DepartedTrip_IsRefusedWithItsCodeAndDepartureTime()
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

    private static Caller Customer(string accountId)
    {
        return new Caller(accountId, "priya@example.com", "Customer", null);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid bookingId, string token)
    {
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/bookings/{bookingId}/cancellationPreview", token);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private PreviewBookingCancellationHandler CreateHandler(DateTimeOffset now)
    {
        IReaderConnectionFactory readerFactory = TestWriters.CreateReader(_database);
        var settings = new FixedBookingSettings();
        var clock = new FixedClock(now);
        return new PreviewBookingCancellationHandler(readerFactory, settings, clock);
    }

    private ApiFactory CreateHost()
    {
        var clock = new FixedClock(_now);
        var seatInventory = new FakeSeatInventoryClient();
        var payment = new FakePaymentClient();
        var settings = new FixedBookingSettings();
        return BookingTestHost.For(_database, BookingTestHost.Doubles(clock, seatInventory, payment, settings));
    }

    // C-1's booking of 3A at 600 and 3B at 499 (total 1099), on a trip leaving the given time after now.
    private async Task<Guid> SeedAsync(BookingStatus status, TimeSpan untilDeparture)
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { DepartureAt = _now + untilDeparture };
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = CustomerId,
            Status = status,
            PaymentId = Guid.NewGuid(),
            ConfirmedAt = _now.AddDays(-1),
            FailureReason = FailureReasonFor(status),
            Passengers = new List<SeedPassenger>
            {
                new SeedPassenger("3A", "Priya Shah", 600),
                new SeedPassenger("3B", "Ravi Shah", 499),
            },
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }

    private static FailureReason? FailureReasonFor(BookingStatus status)
    {
        if (status == BookingStatus.Failed)
        {
            return FailureReason.Abandoned;
        }

        return null;
    }
}
