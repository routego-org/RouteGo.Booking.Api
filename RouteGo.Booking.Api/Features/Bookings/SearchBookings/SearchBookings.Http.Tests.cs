using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace RouteGo.Booking.Api.Features.Bookings.SearchBookings;

[Collection(DatabaseCollection.Name)]
public sealed class SearchBookingsHttpTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public SearchBookingsHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task SearchBookings_AdminSearchesByEmail_Returns200WithThePageShape()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec { TripId = trip.TripId, HoldExpiresAt = _now.AddMinutes(5) };
        await BookingSeed.SeedBookingAsync(_database, booking);
        string token = TestAccessTokens.CreatePerson("Admin", "A-1", "admin@routego.in");
        var search = new { contactEmail = "PRIYA@example.com" };

        // Act
        using HttpResponseMessage response = await SendAsync(token, "/bookings/search", search);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(1, root.GetProperty("totalItems").GetInt32());
        Assert.Equal(20, root.GetProperty("pageSize").GetInt32());
        JsonElement item = root.GetProperty("items")[0];
        Assert.Equal(booking.BookingId, item.GetProperty("bookingId").GetGuid());
        Assert.Equal("paymentPending", item.GetProperty("status").GetString());
        Assert.Equal("Priya Shah", item.GetProperty("contactName").GetString());
        Assert.Equal("priya@example.com", item.GetProperty("contactEmail").GetString());
        Assert.Equal("Mumbai", item.GetProperty("trip").GetProperty("sourceCityName").GetString());
        Assert.Equal("3B", item.GetProperty("seatNos")[1].GetString());
    }

    [Fact]
    public async Task SearchBookings_NoCriterion_IsRefusedValidationFailed()
    {
        // Arrange
        string token = TestAccessTokens.CreatePerson("Admin", "A-1", "admin@routego.in");

        // Act
        using HttpResponseMessage response = await SendAsync(token, "/bookings/search", new { });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("/bookings/search?pageSize=0")]
    [InlineData("/bookings/search?pageSize=201")]
    [InlineData("/bookings/search?page=0")]
    public async Task SearchBookings_PageOrPageSizeOutOfRange_IsRefusedValidationFailed(string route)
    {
        // Arrange
        string token = TestAccessTokens.CreatePerson("Admin", "A-1", "admin@routego.in");

        // Act
        using HttpResponseMessage response = await SendAsync(token, route, new { tripId = Guid.NewGuid() });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Operator")]
    public async Task SearchBookings_NotAnAdmin_IsRefusedForbidden(string role)
    {
        // Arrange
        string token = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendAsync(token, "/bookings/search", new { tripId = Guid.NewGuid() });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task SearchBookings_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent("{\"tripId\":null}", System.Text.Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync("/bookings/search", content);

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

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string route, object body)
    {
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, route, accessToken, body);
        return await client.SendAsync(request);
    }
}
