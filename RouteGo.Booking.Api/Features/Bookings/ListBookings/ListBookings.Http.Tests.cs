using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace RouteGo.Booking.Api.Features.Bookings.ListBookings;

[Collection(DatabaseCollection.Name)]
public sealed class ListBookingsHttpTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public ListBookingsHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ListBookings_CustomerWithAConfirmedBooking_Returns200WithThePageShape()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec { TripId = trip.TripId, Status = BookingStatus.Confirmed, ConfirmedAt = _now };
        await BookingSeed.SeedBookingAsync(_database, booking);
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(token, "/bookings");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(20, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, root.GetProperty("totalItems").GetInt32());
        JsonElement item = root.GetProperty("items")[0];
        Assert.Equal(booking.BookingId, item.GetProperty("bookingId").GetGuid());
        Assert.Equal("confirmed", item.GetProperty("status").GetString());
        Assert.Equal("INR", item.GetProperty("currency").GetString());
        Assert.Equal(1600, item.GetProperty("totalAmount").GetInt64());
        Assert.Equal("3A", item.GetProperty("seatNos")[0].GetString());
        Assert.Equal("Pune", item.GetProperty("trip").GetProperty("destinationCityName").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("refund").ValueKind);
    }

    [Theory]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=201")]
    [InlineData("?page=0")]
    public async Task ListBookings_PageOrPageSizeOutOfRange_IsRefusedValidationFailed(string query)
    {
        // Arrange
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/bookings{query}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Operator")]
    public async Task ListBookings_NotACustomer_IsRefusedForbidden(string role)
    {
        // Arrange
        string token = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendAsync(token, "/bookings");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListBookings_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/bookings");

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

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string route)
    {
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, route, accessToken);
        return await client.SendAsync(request);
    }
}
