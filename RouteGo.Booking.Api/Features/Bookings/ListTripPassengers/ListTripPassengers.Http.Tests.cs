using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace RouteGo.Booking.Api.Features.Bookings.ListTripPassengers;

[Collection(DatabaseCollection.Name)]
public sealed class ListTripPassengersHttpTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public ListTripPassengersHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ListTripPassengers_OwnOperator_Returns200WithPassengersAndNeverTheContact()
    {
        // Arrange
        Guid tripId = await SeedTripWithConfirmedBookingAsync("OP-1");
        string token = TestAccessTokens.CreateOperator("OP-1");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/trips/{tripId}/passengers");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("contact", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("priya@example.com", text, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(text);
        JsonElement root = body.RootElement;
        Assert.Equal(2, root.GetProperty("seatsBooked").GetInt32());
        Assert.Equal(tripId, root.GetProperty("trip").GetProperty("tripId").GetGuid());
        Assert.Equal("2099-06-01", root.GetProperty("trip").GetProperty("serviceDate").GetString());
        JsonElement first = root.GetProperty("passengers")[0];
        Assert.Equal("3A", first.GetProperty("seatNo").GetString());
        Assert.Equal("Sleeper", first.GetProperty("seatClass").GetString());
        Assert.Equal("Priya Shah", first.GetProperty("passengerName").GetString());
        Assert.Equal(30, first.GetProperty("passengerAge").GetInt32());
        Assert.Equal("female", first.GetProperty("passengerGender").GetString());
    }

    [Fact]
    public async Task ListTripPassengers_AnotherOperator_IsRefusedForbidden()
    {
        // Arrange
        Guid tripId = await SeedTripWithConfirmedBookingAsync("OP-2");
        string token = TestAccessTokens.CreateOperator("OP-1");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/trips/{tripId}/passengers");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Admin")]
    public async Task ListTripPassengers_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        Guid tripId = await SeedTripWithConfirmedBookingAsync("OP-1");
        string token = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/trips/{tripId}/passengers");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListTripPassengers_UnknownTrip_IsRefusedNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        string token = TestAccessTokens.CreateOperator("OP-1");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/trips/{Guid.NewGuid()}/passengers");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListTripPassengers_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/trips/{Guid.NewGuid()}/passengers");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private ApiFactory CreateHost()
    {
        Action<IServiceCollection> doubles = BookingTestHost.Doubles(
            new FixedClock(_now), new FakeSeatInventoryClient(), new FakePaymentClient(), new FixedBookingSettings());
        return BookingTestHost.For(_database, doubles);
    }

    private async Task<Guid> SeedTripWithConfirmedBookingAsync(string operatorId)
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed { OperatorId = operatorId };
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Confirmed, ConfirmedAt = _now };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return trip.TripId;
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string route)
    {
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, route, accessToken);
        return await client.SendAsync(request);
    }
}
