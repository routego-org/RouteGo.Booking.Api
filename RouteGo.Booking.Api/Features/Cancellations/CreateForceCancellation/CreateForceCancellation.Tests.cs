using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Cancellations.CreateForceCancellation;

[Collection(DatabaseCollection.Name)]
public sealed class CreateForceCancellationTests
{
    // 2026-10-25 at 12:00 UTC.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _paymentId = Guid.Parse("0a0a0a0a-0000-4000-8000-0000000000b9");

    private const string Reason = "Bus did not arrive at the stop";
    private const string AdminId = "A-1";
    private const string CorrelationId = "force-cancel-test";

    private const string StatusSql = "SELECT status FROM bookings.bookings WHERE booking_id = @BookingId";

    private const string CancellationSql = """
        SELECT kind || '/' || reason || '/' || refund_percent || '/' || refund_amount || '/' || cancelled_by
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

    private const string InsertCustomerCancellationSql = """
        UPDATE bookings.bookings SET status = 'Cancelled' WHERE booking_id = @BookingId;
        INSERT INTO cancellations.cancellations (booking_id, kind, reason, refund_percent, refund_amount, cancelled_by, cancelled_at)
        VALUES (@BookingId, 'CustomerCancelled', NULL, 0, 0, 'C-1', @CancelledAt)
        """;

    private readonly DatabaseFixture _database;

    public CreateForceCancellationTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_NoPercentageAfterDeparture_CancelsWithAFullRefund()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-40), 800, 800);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.Cancelled, result.Outcome);
        Cancellation cancellation = result.Response!;
        Assert.Equal(CancellationKind.AdminCancelled, cancellation.Kind);
        Assert.Equal(Reason, cancellation.Reason);
        Assert.Equal(100, cancellation.RefundPercent);
        Assert.Equal(1600, cancellation.RefundAmount);
        Assert.Equal(RefundStatus.Requested, cancellation.RefundStatus);
        Assert.Equal("Cancelled", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
        Assert.Equal($"AdminCancelled/{Reason}/100/1600/A-1", await _database.QueryScalarAsync<string>(CancellationSql, new { BookingId = bookingId }));
        string expectedRefund = $"Cancellation/1600/{bookingId}:cancel/Requested/{_paymentId}";
        Assert.Equal(expectedRefund, await _database.QueryScalarAsync<string>(RefundSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_ForceCancel_RecordsCancelledRefundRequestedAndAuditEventsWithTheReason()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-40), 800, 800);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal("BookingCancelled,RefundRequested,AuditRecorded", await EventTypesAsync(bookingId));
        JsonElement cancelled = await EventDataAsync(bookingId, "BookingCancelled");
        Assert.Equal("AdminCancelled", cancelled.GetProperty("kind").GetString());
        Assert.Equal(Reason, cancelled.GetProperty("reason").GetString());
        Assert.Equal(100, cancelled.GetProperty("refundPercent").GetInt32());
        Assert.Equal(1600, cancelled.GetProperty("refundAmount").GetInt64());
        Assert.Equal("Mumbai", cancelled.GetProperty("trip").GetProperty("source").GetString());
        JsonElement refund = await EventDataAsync(bookingId, "RefundRequested");
        Assert.Equal(1600, refund.GetProperty("amount").GetInt64());
        Assert.Equal($"{bookingId}:cancel", refund.GetProperty("idempotencyKey").GetString());
        JsonElement audit = await EventDataAsync(bookingId, "AuditRecorded");
        Assert.Equal(AdminId, audit.GetProperty("actorId").GetString());
        Assert.Equal("Admin", audit.GetProperty("actorRole").GetString());
        Assert.Equal("BookingForceCancelled", audit.GetProperty("action").GetString());
        Assert.Equal(bookingId, audit.GetProperty("entityId").GetGuid());
        Assert.Equal(Reason, audit.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HandleAsync_FiftyPercent_RefundsHalfRoundedDown()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 600, 499);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, 50), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(50, result.Response!.RefundPercent);
        Assert.Equal(549, result.Response!.RefundAmount);
        Assert.Equal($"AdminCancelled/{Reason}/50/549/A-1", await _database.QueryScalarAsync<string>(CancellationSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_ZeroPercent_CancelsWithNoRefundRecordedOrRequested()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 600, 499);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, 0), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, result.Response!.RefundAmount);
        Assert.Null(result.Response!.RefundStatus);
        Assert.Equal(0, await _database.QueryScalarAsync<int>(RefundCountSql, new { BookingId = bookingId }));
        Assert.Equal("BookingCancelled,AuditRecorded", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_ExactlySevenDaysAfterDeparture_IsStillAllowed()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromDays(-7), 800, 800);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_OneMinutePastSevenDays_IsRefusedWithTheDepartureAndTheWindow()
    {
        // Arrange
        TimeSpan sevenDaysAndAMinute = TimeSpan.FromDays(-7) - TimeSpan.FromMinutes(1);
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, sevenDaysAndAMinute, 800, 800);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.ForceCancelWindowClosed, result.Outcome);
        Assert.Equal(_now + sevenDaysAndAMinute, result.DepartedAt);
        Assert.Equal(7, result.WindowDays);
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
        Assert.Equal("-", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_PaymentPendingBooking_IsRefusedNamingItsStatus()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.PaymentPending, TimeSpan.FromHours(30), 800, 800);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.BookingNotConfirmed, result.Outcome);
        Assert.Equal(BookingStatus.PaymentPending, result.BookingStatus);
    }

    [Fact]
    public async Task HandleAsync_BookingCancelledByItsCustomer_IsRefusedNotConfirmed()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 800, 800);
        await _database.SeedAsync(InsertCustomerCancellationSql, new { BookingId = bookingId, CancelledAt = _now.UtcDateTime });
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.BookingNotConfirmed, result.Outcome);
        Assert.Equal(BookingStatus.Cancelled, result.BookingStatus);
        Assert.Equal("-", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_ForceCancellingAgain_ReturnsTheExistingCancellationAndRecordsNothingNew()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-40), 800, 800);
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());
        CreateForceCancellationResult first = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);
        var different = new ForceCancellation(bookingId, "A different reason entirely", 10);

        // Act
        CreateForceCancellationResult repeat = await handler.HandleAsync(different, Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.AlreadyCancelled, repeat.Outcome);
        Assert.Equal(first.Response, repeat.Response);
        Assert.Equal("BookingCancelled,RefundRequested,AuditRecorded", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        CreateForceCancellationHandler handler = CreateHandler(new FixedBookingSettings());

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(Guid.NewGuid(), null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WindowSettingChanged_TheNewWindowApplies()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromDays(-3), 800, 800);
        var settings = new FixedBookingSettings();
        settings.Current = BookingSettingsValues.CreateDefault() with { ForceCancelWindowDays = 2 };
        CreateForceCancellationHandler handler = CreateHandler(settings);

        // Act
        CreateForceCancellationResult result = await handler.HandleAsync(Request(bookingId, null), Admin(), CorrelationId, CancellationToken.None);

        // Assert
        Assert.Equal(CreateForceCancellationOutcome.ForceCancelWindowClosed, result.Outcome);
        Assert.Equal(2, result.WindowDays);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(501)]
    public async Task Validate_ReasonOfTheWrongLength_IsRefusedNamingTheReason(int length)
    {
        // Arrange
        var validator = new CreateForceCancellationValidator();
        var request = new ForceCancellationRequest { Reason = new string('x', length) };

        // Act
        ValidationResult result = await validator.ValidateAsync(request);

        // Assert
        Assert.Equal("reason", result.Errors.Single().PropertyName, ignoreCase: true);
    }

    [Fact]
    public async Task Validate_MissingReason_IsRefusedNamingTheReason()
    {
        // Arrange
        var validator = new CreateForceCancellationValidator();
        var request = new ForceCancellationRequest { Reason = null };

        // Act
        ValidationResult result = await validator.ValidateAsync(request);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("reason", result.Errors.Single().PropertyName, ignoreCase: true);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    [InlineData(12.5)]
    public async Task Validate_PercentageThatIsNotAWholeNumberFromZeroToHundred_IsRefused(double percent)
    {
        // Arrange
        var validator = new CreateForceCancellationValidator();
        var request = new ForceCancellationRequest { Reason = Reason, RefundPercent = (decimal)percent };

        // Act
        ValidationResult result = await validator.ValidateAsync(request);

        // Assert
        Assert.Equal("refundPercent", result.Errors.Single().PropertyName, ignoreCase: true);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(50.0)]
    [InlineData(100.0)]
    public async Task Validate_WholePercentageFromZeroToHundredAndAReasonAtTheLimits_IsAccepted(double percent)
    {
        // Arrange
        var validator = new CreateForceCancellationValidator();
        var request = new ForceCancellationRequest { Reason = new string('x', 10), RefundPercent = (decimal)percent };

        // Act
        ValidationResult result = await validator.ValidateAsync(request);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ForceCancel_WithoutAnAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        using var content = JsonContent.Create(new { reason = Reason });

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/bookings/{bookingId}/forceCancellations", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Operator")]
    public async Task ForceCancel_CallerThatIsNotAnAdmin_IsRefusedForbiddenAndNothingChanges(string role)
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task ForceCancel_UnknownBooking_IsRefusedNotFoundWithItsCode()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, Guid.NewGuid(), token, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ForceCancel_ConfirmedBooking_ReturnsCreatedWithTheCancellationAndTheBookingsLocation()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-40), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/bookings/{bookingId}", response.Headers.Location!.OriginalString);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(bookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal("adminCancelled", root.GetProperty("kind").GetString());
        Assert.Equal(Reason, root.GetProperty("reason").GetString());
        Assert.Equal(100, root.GetProperty("refundPercent").GetInt32());
        Assert.Equal(1600, root.GetProperty("refundAmount").GetInt64());
        Assert.Equal("INR", root.GetProperty("currency").GetString());
        Assert.Equal(_now, root.GetProperty("cancelledAt").GetDateTimeOffset());
        Assert.Equal("requested", root.GetProperty("refundStatus").GetString());
    }

    [Fact]
    public async Task ForceCancel_RepeatThroughTheHost_ReturnsOkWithTheSameCancellation()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromMinutes(-40), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");
        using HttpResponseMessage first = await SendAsync(client, bookingId, token, new { reason = Reason });

        // Act
        using HttpResponseMessage repeat = await SendAsync(client, bookingId, token, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForceCancel_PercentageOfTwelvePointFive_IsRefusedValidationFailedNamingTheField()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");
        var body = new { reason = Reason, refundPercent = 12.5 };

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("refundPercent", out JsonElement _));
        Assert.Equal("Confirmed", await _database.QueryScalarAsync<string>(StatusSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task ForceCancel_ShortReason_IsRefusedValidationFailedNamingTheField()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, TimeSpan.FromHours(30), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, new { reason = "too short" });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("reason", out JsonElement _));
    }

    [Fact]
    public async Task ForceCancel_DepartedMoreThanSevenDaysAgo_IsRefusedWithItsCodeDepartureAndWindow()
    {
        // Arrange
        TimeSpan sevenDaysAndAMinute = TimeSpan.FromDays(-7) - TimeSpan.FromMinutes(1);
        Guid bookingId = await SeedAsync(BookingStatus.Confirmed, sevenDaysAndAMinute, 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("FORCE_CANCEL_WINDOW_CLOSED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(_now + sevenDaysAndAMinute, problem.RootElement.GetProperty("departedAt").GetDateTimeOffset());
        Assert.Equal(7, problem.RootElement.GetProperty("windowDays").GetInt32());
    }

    [Fact]
    public async Task ForceCancel_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus()
    {
        // Arrange
        Guid bookingId = await SeedAsync(BookingStatus.PaymentPending, TimeSpan.FromHours(30), 800, 800);
        using ApiFactory factory = CreateHost(new FixedBookingSettings());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Admin", AdminId, "admin@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, new { reason = Reason });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BOOKING_NOT_CONFIRMED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("paymentPending", problem.RootElement.GetProperty("bookingStatus").GetString());
    }

    private static Caller Admin()
    {
        return new Caller(AdminId, "admin@example.com", "Admin", null);
    }

    private static ForceCancellation Request(Guid bookingId, int? refundPercent)
    {
        return new ForceCancellation(bookingId, Reason, refundPercent);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid bookingId, string token, object body)
    {
        using HttpRequestMessage request = TestAccessTokens.Authorized(
            HttpMethod.Post, $"/bookings/{bookingId}/forceCancellations", token, body);
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

    private CreateForceCancellationHandler CreateHandler(FixedBookingSettings settings)
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(_database);
        var clock = new FixedClock(_now);
        return new CreateForceCancellationHandler(writerFactory, settings, clock, NullLogger<CreateForceCancellationHandler>.Instance);
    }

    private ApiFactory CreateHost(FixedBookingSettings settings)
    {
        var clock = new FixedClock(_now);
        var seatInventory = new FakeSeatInventoryClient();
        var payment = new FakePaymentClient();
        return BookingTestHost.For(_database, BookingTestHost.Doubles(clock, seatInventory, payment, settings));
    }

    // C-1's booking of 3A and 3B at the given fares, paid by payment b9, on a trip leaving the given time after now.
    private async Task<Guid> SeedAsync(BookingStatus status, TimeSpan untilDeparture, long firstFare, long secondFare)
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { DepartureAt = _now + untilDeparture };
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = "C-1",
            Status = status,
            PaymentId = _paymentId,
            ConfirmedAt = _now.AddDays(-30),
            Passengers = new List<SeedPassenger>
            {
                new SeedPassenger("3A", "Priya Shah", firstFare),
                new SeedPassenger("3B", "Ravi Shah", secondFare),
            },
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }
}
