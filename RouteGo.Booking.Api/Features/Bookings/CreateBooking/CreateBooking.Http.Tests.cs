using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;

namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

[Collection(DatabaseCollection.Name)]
public sealed class CreateBookingHttpTests
{
    private const string Contact = "\"contactName\":\"Priya Shah\"";

    private readonly DatabaseFixture _database;

    public CreateBookingHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateBooking_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync("/bookings", Json(OneSeatBody(trip.TripId, "\"female\"")));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Admin")]
    public async Task CreateBooking_CallerIsNotACustomer_IsRefusedForbiddenAndHoldsNothing(string role)
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson(role, "A-1", "someone@example.com");

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, OneSeatBody(trip.TripId, "\"female\""));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
        Assert.Empty(scenario.SeatInventory.Holds);
    }

    [Fact]
    public async Task CreateBooking_ValidRequest_Returns201WithLocationAndTheBooking()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, OneSeatBody(trip.TripId, "\"Female\""));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        string bookingId = root.GetProperty("bookingId").GetString()!;
        Assert.Equal($"/bookings/{bookingId}", response.Headers.Location!.OriginalString);
        Assert.Equal("paymentPending", root.GetProperty("status").GetString());
        Assert.Equal("INR", root.GetProperty("currency").GetString());
        Assert.Equal(800, root.GetProperty("totalAmount").GetInt64());
        Assert.Equal(FakePaymentClient.DefaultCheckoutUrl, root.GetProperty("checkoutUrl").GetString());
        Assert.Equal(CreateBookingScenario.Now.AddSeconds(600), root.GetProperty("holdExpiresAt").GetDateTimeOffset());
        Assert.Equal("priya@example.com", root.GetProperty("contactEmail").GetString());
        Assert.Equal(0, root.GetProperty("seatChangeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cancellation").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("refund").ValueKind);
        JsonElement seat = root.GetProperty("seats")[0];
        Assert.Equal("female", seat.GetProperty("passengerGender").GetString());
        Assert.Equal("Sleeper", seat.GetProperty("seatClass").GetString());
        JsonElement tripSummary = root.GetProperty("trip");
        Assert.Equal("sleeper", tripSummary.GetProperty("busType").GetString());
        Assert.Equal("Sharma Travels", tripSummary.GetProperty("operatorName").GetString());
        Assert.Equal("2099-06-01", tripSummary.GetProperty("serviceDate").GetString());
    }

    [Fact]
    public async Task CreateBooking_FareChanged_RefusesPriceChangedWithCurrentTotalAndSeatFares()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");
        string body = OneSeatBody(trip.TripId, "\"female\"").Replace("\"expectedTotal\":800", "\"expectedTotal\":700");

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PRICE_CHANGED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(800, problem.RootElement.GetProperty("currentTotal").GetInt64());
        JsonElement fare = problem.RootElement.GetProperty("seatFares")[0];
        Assert.Equal("3A", fare.GetProperty("seatNo").GetString());
        Assert.Equal(800, fare.GetProperty("fare").GetInt32());
    }

    [Fact]
    public async Task CreateBooking_SeatInventoryTimesOut_Returns504WithRetryAfter()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.SeatInventory.HoldResult = new HoldSeatsResult { Outcome = HoldSeatsOutcome.TimedOut };
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, OneSeatBody(trip.TripId, "\"female\""));

        // Assert
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UPSTREAM_TIMEOUT", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(5, problem.RootElement.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task CreateBooking_SeatNotOnTheTrip_Returns422NamingTheSeat()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");
        string body = OneSeatBody(trip.TripId, "\"female\"").Replace("3A", "Z99");

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNKNOWN_SEATS", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Z99", problem.RootElement.GetProperty("unknownSeats")[0].GetString());
    }

    [Theory]
    [InlineData("\"seats\":[]", "seats")]
    [InlineData("\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"Ann Lee\",\"passengerAge\":30,\"passengerGender\":\"female\"},{\"seatNo\":\"3A\",\"passengerName\":\"Bo Lee\",\"passengerAge\":30,\"passengerGender\":\"male\"}]", "seats")]
    [InlineData("\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"A\",\"passengerAge\":30,\"passengerGender\":\"female\"}]", "seats[0].passengerName")]
    [InlineData("\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"Ann Lee\",\"passengerAge\":-1,\"passengerGender\":\"female\"}]", "seats[0].passengerAge")]
    [InlineData("\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"Ann Lee\",\"passengerAge\":121,\"passengerGender\":\"female\"}]", "seats[0].passengerAge")]
    [InlineData("\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"Ann Lee\",\"passengerAge\":4.5,\"passengerGender\":\"female\"}]", "seats[0].passengerAge")]
    [InlineData("\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"Ann Lee\",\"passengerAge\":30,\"passengerGender\":\"robot\"}]", "seats[0].passengerGender")]
    public async Task CreateBooking_BadSeats_IsRefusedValidationFailedNamingTheField(string seatsJson, string field)
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");
        string body = $"{{\"tripId\":\"{trip.TripId}\",{Contact},{seatsJson},\"expectedTotal\":800}}";

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
        Assert.Empty(scenario.SeatInventory.Holds);
    }

    [Theory]
    [InlineData("\"contactName\":\"P\"", "contactName")]
    [InlineData("\"other\":1", "contactName")]
    public async Task CreateBooking_BadContact_IsRefusedValidationFailedNamingTheField(string contactJson, string field)
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");
        string body = OneSeatBody(trip.TripId, "\"female\"").Replace(Contact, contactJson);

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Fact]
    public async Task CreateBooking_NoExpectedTotal_IsRefusedValidationFailedNamingTheField()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");
        string body = OneSeatBody(trip.TripId, "\"female\"").Replace(",\"expectedTotal\":800", string.Empty);

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("expectedTotal", out _), "No error for expectedTotal.");
    }

    [Fact]
    public async Task CreateBooking_MoreSeatsThanTheSettingAllows_IsRefusedValidationFailed()
    {
        // Arrange
        (CreateBookingScenario scenario, TripSeed trip) = await SeedAsync();
        scenario.Settings.Current = scenario.Settings.Current with { MaxSeats = 1 };
        using ApiFactory factory = CreateHost(scenario);
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");
        string twoSeats = "\"seats\":[{\"seatNo\":\"3A\",\"passengerName\":\"Ann Lee\",\"passengerAge\":30,\"passengerGender\":\"female\"},"
            + "{\"seatNo\":\"3B\",\"passengerName\":\"Bo Lee\",\"passengerAge\":30,\"passengerGender\":\"male\"}]";
        string body = $"{{\"tripId\":\"{trip.TripId}\",{Contact},{twoSeats},\"expectedTotal\":1600}}";

        // Act
        using HttpResponseMessage response = await PostAsync(client, token, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("seats", out _), "No error for seats.");
    }

    private static string OneSeatBody(Guid tripId, string genderJson)
    {
        return $"{{\"tripId\":\"{tripId}\",{Contact},\"seats\":[{{\"seatNo\":\"3A\",\"passengerName\":\"Priya Shah\","
            + $"\"passengerAge\":34,\"passengerGender\":{genderJson}}}],\"expectedTotal\":800}}";
    }

    private static StringContent Json(string body)
    {
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/bookings") { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private ApiFactory CreateHost(CreateBookingScenario scenario)
    {
        return BookingTestHost.For(
            _database,
            BookingTestHost.Doubles(scenario.Clock, scenario.SeatInventory, scenario.Payment, scenario.Settings));
    }

    private async Task<(CreateBookingScenario Scenario, TripSeed Trip)> SeedAsync()
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        return (new CreateBookingScenario(_database, CreateBookingScenario.Now), trip);
    }
}
