using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation.Results;

namespace RouteGo.Booking.Api.Features.Bookings.CreateSeatChange;

[Collection(DatabaseCollection.Name)]
public sealed class CreateSeatChangeHttpTests
{
    private const string CustomerEmail = "priya@example.com";

    private readonly DatabaseFixture _database;

    public CreateSeatChangeHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task SeatChange_WithoutAnAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();
        using var content = JsonContent.Create(OneMove("4C", "5C"));

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/bookings/{bookingId}/seatChanges", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    public async Task SeatChange_CallerThatIsNotACustomer_IsRefusedForbidden(string role)
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, TestAccessTokens.Create(role), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SeatChange_AnotherCustomersBooking_IsRefusedForbiddenWithItsCode()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-2", "other@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, token, OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task SeatChange_UnknownBooking_IsRefusedNotFoundWithItsCode()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, Guid.NewGuid(), CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task SeatChange_FreeSeat_ReturnsCreatedWithTheSeatsAfterTheChangeAndTheBookingsLocation()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/bookings/{bookingId}", response.Headers.Location!.OriginalString);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(bookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal("4C", root.GetProperty("changes")[0].GetProperty("fromSeatNo").GetString());
        Assert.Equal("5C", root.GetProperty("changes")[0].GetProperty("toSeatNo").GetString());
        Assert.Equal(1, root.GetProperty("seatChangeCount").GetInt32());
        JsonElement movedSeat = root.GetProperty("seats")[1];
        Assert.Equal("5C", movedSeat.GetProperty("seatNo").GetString());
        Assert.Equal("Seater", movedSeat.GetProperty("seatClass").GetString());
        Assert.Equal(600, movedSeat.GetProperty("fare").GetInt64());
        Assert.Equal("Sameer Rao", movedSeat.GetProperty("passengerName").GetString());
        Assert.Equal(30, movedSeat.GetProperty("passengerAge").GetInt32());
        Assert.Equal("female", movedSeat.GetProperty("passengerGender").GetString());
    }

    [Fact]
    public async Task SeatChange_RepeatThroughTheHost_ReturnsOkWithNoChanges()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage first = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Act
        using HttpResponseMessage repeat = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        using var body = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("changes").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("seatChangeCount").GetInt32());
    }

    [Fact]
    public async Task SeatChange_NoMoves_IsRefusedValidationFailedNamingTheField()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), new { changes = Array.Empty<object>() });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("changes", out JsonElement _));
    }

    [Fact]
    public async Task SeatChange_NewSeatTaken_IsRefusedSeatsUnavailableNamingTheSeat()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        var seatInventory = new FakeSeatInventoryClient();
        seatInventory.ChangeResult = new ChangeSeatsResult
        {
            Outcome = ChangeSeatsOutcome.SeatsUnavailable,
            NamedSeats = new List<string> { "5D" },
        };
        using ApiFactory factory = CreateHost(seatInventory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4D", "5D"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SEATS_UNAVAILABLE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("5D", problem.RootElement.GetProperty("unavailableSeats")[0].GetString());
    }

    [Fact]
    public async Task SeatChange_SeatOfAnotherClass_IsRefusedSeatClassMismatchNamingTheMove()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "L3"));

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SEAT_CLASS_MISMATCH", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("4C", problem.RootElement.GetProperty("changes")[0].GetProperty("fromSeatNo").GetString());
        Assert.Equal("L3", problem.RootElement.GetProperty("changes")[0].GetProperty("toSeatNo").GetString());
    }

    [Fact]
    public async Task SeatChange_LimitReached_IsRefusedWithItsCodeAndTheLimit()
    {
        // Arrange
        Guid bookingId = await SeedAsync(3);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("SEAT_CHANGE_LIMIT_REACHED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(3, problem.RootElement.GetProperty("maxSeatChanges").GetInt32());
    }

    [Fact]
    public async Task SeatChange_SeatInventoryDown_IsRefusedBadGatewayWithItsCode()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        var seatInventory = new FakeSeatInventoryClient();
        seatInventory.ChangeResult = new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Unavailable };
        using ApiFactory factory = CreateHost(seatInventory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UPSTREAM_UNAVAILABLE", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task SeatChange_SeatInventorySlow_IsRefusedGatewayTimeoutWithItsCode()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0);
        var seatInventory = new FakeSeatInventoryClient();
        seatInventory.ChangeResult = new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.TimedOut };
        using ApiFactory factory = CreateHost(seatInventory);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UPSTREAM_TIMEOUT", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(5, problem.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task SeatChange_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0, BookingStatus.PaymentPending);
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BOOKING_NOT_CONFIRMED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("paymentPending", problem.RootElement.GetProperty("bookingStatus").GetString());
    }

    [Fact]
    public async Task SeatChange_ChangesClosed_IsRefusedWithItsCodeDepartureAndCutoff()
    {
        // Arrange
        Guid bookingId = await SeedAsync(0, BookingStatus.Confirmed, TimeSpan.FromMinutes(29));
        using ApiFactory factory = CreateHost(new FakeSeatInventoryClient());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, bookingId, CustomerToken(), OneMove("4C", "5C"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BOOKING_CLOSED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(CreateSeatChangeTests.Now.AddMinutes(29), problem.RootElement.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal(30, problem.RootElement.GetProperty("cutoffMinutes").GetInt32());
    }

    [Fact]
    public async Task Validate_OneAndSixMoves_AreAccepted()
    {
        // Arrange
        var validator = new CreateSeatChangeValidator();
        CreateSeatChangeRequest one = Request(Pair("1A", "2A"));
        CreateSeatChangeRequest six = Request(
            Pair("1A", "2A"), Pair("1B", "2B"), Pair("1C", "2C"), Pair("1D", "2D"), Pair("1E", "2E"), Pair("1F", "2F"));

        // Act
        ValidationResult oneResult = await validator.ValidateAsync(one);
        ValidationResult sixResult = await validator.ValidateAsync(six);

        // Assert
        Assert.True(oneResult.IsValid);
        Assert.True(sixResult.IsValid);
    }

    [Fact]
    public async Task Validate_SwapOfTwoSeats_IsAccepted()
    {
        // Arrange
        var validator = new CreateSeatChangeValidator();
        CreateSeatChangeRequest swap = Request(Pair("4C", "4D"), Pair("4D", "4C"));

        // Act
        ValidationResult result = await validator.ValidateAsync(swap);

        // Assert
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Validate_NoMovesAtAll_IsRefusedNamingChanges()
    {
        // Arrange
        var validator = new CreateSeatChangeValidator();

        // Act
        ValidationResult missing = await validator.ValidateAsync(new CreateSeatChangeRequest());
        ValidationResult empty = await validator.ValidateAsync(Request());

        // Assert
        Assert.Equal("changes", missing.Errors.First().PropertyName, ignoreCase: true);
        Assert.Equal("changes", empty.Errors.First().PropertyName, ignoreCase: true);
    }

    [Fact]
    public async Task Validate_SevenMoves_IsRefusedNamingChanges()
    {
        // Arrange
        var validator = new CreateSeatChangeValidator();
        CreateSeatChangeRequest seven = Request(
            Pair("1A", "2A"), Pair("1B", "2B"), Pair("1C", "2C"), Pair("1D", "2D"), Pair("1E", "2E"), Pair("1F", "2F"), Pair("1G", "2G"));

        // Act
        ValidationResult result = await validator.ValidateAsync(seven);

        // Assert
        Assert.Equal("changes", result.Errors.First().PropertyName, ignoreCase: true);
    }

    [Fact]
    public async Task Validate_SeatLeftTwiceOrMovedToTwice_IsRefusedNamingChanges()
    {
        // Arrange
        var validator = new CreateSeatChangeValidator();
        CreateSeatChangeRequest leftTwice = Request(Pair("1A", "2A"), Pair("1A", "2B"));
        CreateSeatChangeRequest movedToTwice = Request(Pair("1A", "2A"), Pair("1B", "2A"));

        // Act
        ValidationResult leftResult = await validator.ValidateAsync(leftTwice);
        ValidationResult toResult = await validator.ValidateAsync(movedToTwice);

        // Assert
        Assert.Equal("changes", leftResult.Errors.Single().PropertyName, ignoreCase: true);
        Assert.Equal("changes", toResult.Errors.Single().PropertyName, ignoreCase: true);
    }

    [Fact]
    public async Task Validate_MoveFromASeatToItself_IsRefusedNamingTheMove()
    {
        // Arrange
        var validator = new CreateSeatChangeValidator();
        CreateSeatChangeRequest request = Request(Pair("1A", "1A"));

        // Act
        ValidationResult result = await validator.ValidateAsync(request);

        // Assert
        Assert.Contains("changes[0]", result.Errors.Single().PropertyName, StringComparison.OrdinalIgnoreCase);
    }

    private static CreateSeatChangeRequest Request(params SeatMoveRequest[] moves)
    {
        return new CreateSeatChangeRequest { Changes = moves.Select(move => (SeatMoveRequest?)move).ToList() };
    }

    private static SeatMoveRequest Pair(string from, string to)
    {
        return new SeatMoveRequest { FromSeatNo = from, ToSeatNo = to };
    }

    private static object OneMove(string from, string to)
    {
        return new { changes = new[] { new { fromSeatNo = from, toSeatNo = to } } };
    }

    private static string CustomerToken()
    {
        return TestAccessTokens.CreatePerson("Customer", CreateSeatChangeTests.CustomerId, CustomerEmail);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Guid bookingId, string token, object body)
    {
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/bookings/{bookingId}/seatChanges", token, body);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    private Task<Guid> SeedAsync(int seatChangeCount)
    {
        return SeedAsync(seatChangeCount, BookingStatus.Confirmed);
    }

    private Task<Guid> SeedAsync(int seatChangeCount, BookingStatus status)
    {
        return SeedAsync(seatChangeCount, status, TimeSpan.FromHours(5));
    }

    private Task<Guid> SeedAsync(int seatChangeCount, BookingStatus status, TimeSpan untilDeparture)
    {
        return CreateSeatChangeTests.SeedAsync(_database, status, untilDeparture, seatChangeCount);
    }

    private ApiFactory CreateHost(FakeSeatInventoryClient seatInventory)
    {
        var clock = new FixedClock(CreateSeatChangeTests.Now);
        var payment = new FakePaymentClient();
        var settings = new FixedBookingSettings();
        return BookingTestHost.For(_database, BookingTestHost.Doubles(clock, seatInventory, payment, settings));
    }
}
