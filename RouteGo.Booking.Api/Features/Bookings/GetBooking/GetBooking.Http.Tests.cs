using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace RouteGo.Booking.Api.Features.Bookings.GetBooking;

[Collection(DatabaseCollection.Name)]
public sealed class GetBookingHttpTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public GetBookingHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task GetBooking_OwnerReadsPaymentPendingBooking_Returns200WithTheContractShape()
    {
        // Arrange
        Guid bookingId = await SeedPendingBookingAsync();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/bookings/{bookingId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(bookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal("paymentPending", root.GetProperty("status").GetString());
        Assert.Equal("INR", root.GetProperty("currency").GetString());
        Assert.Equal(1600, root.GetProperty("totalAmount").GetInt64());
        Assert.Equal("http://pay.example/checkout/77", root.GetProperty("checkoutUrl").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("cancellation").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("refund").ValueKind);
        JsonElement trip = root.GetProperty("trip");
        Assert.Equal("sleeper", trip.GetProperty("busType").GetString());
        Assert.Equal("2099-06-01", trip.GetProperty("serviceDate").GetString());
        JsonElement firstSeat = root.GetProperty("seats")[0];
        Assert.Equal("3A", firstSeat.GetProperty("seatNo").GetString());
        Assert.Equal("female", firstSeat.GetProperty("passengerGender").GetString());
    }

    [Fact]
    public async Task GetBooking_AdminReadsACustomersBooking_Returns200()
    {
        // Arrange
        Guid bookingId = await SeedPendingBookingAsync();
        string token = TestAccessTokens.CreatePerson("Admin", "A-1", "admin@routego.in");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/bookings/{bookingId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetBooking_AnotherCustomer_IsRefusedForbidden()
    {
        // Arrange
        Guid bookingId = await SeedPendingBookingAsync();
        string token = TestAccessTokens.CreatePerson("Customer", "C-2", "other@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/bookings/{bookingId}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetBooking_Operator_IsRefusedForbidden()
    {
        // Arrange
        Guid bookingId = await SeedPendingBookingAsync();
        string token = TestAccessTokens.CreateOperator("OP-1");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/bookings/{bookingId}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetBooking_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(token, $"/bookings/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetBooking_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/bookings/{Guid.NewGuid()}");

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

    private async Task<Guid> SeedPendingBookingAsync()
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            HoldExpiresAt = _now.AddMinutes(6),
            CheckoutUrl = "http://pay.example/checkout/77",
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string route)
    {
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, route, accessToken);
        return await client.SendAsync(request);
    }
}
