using System.Text.Json;

namespace RouteGo.Booking.Api.Features.Bookings.ReleaseBooking;

[Collection(DatabaseCollection.Name)]
public sealed class ReleaseBookingHttpTests
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 12, 33, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;
    private readonly FakeSeatInventoryClient _seatInventory = new FakeSeatInventoryClient();

    public ReleaseBookingHttpTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ReleaseBooking_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/bookings/{booking.BookingId}/release", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Customer", "C-2")]
    [InlineData("Admin", "A-1")]
    [InlineData("Operator", "OP-1")]
    public async Task ReleaseBooking_NotTheBookingsCustomer_IsRefusedForbiddenAndReleasesNothing(string role, string accountId)
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson(role, accountId, "other@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, token, booking.BookingId);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
        Assert.Empty(_seatInventory.Releases);
    }

    [Fact]
    public async Task ReleaseBooking_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        await SeedAsync(BookingStatus.PaymentPending);
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, token, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReleaseBooking_OwnUnpaidBooking_Returns200WithTheAbandonedBooking()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.PaymentPending);
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, token, booking.BookingId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(booking.BookingId, root.GetProperty("bookingId").GetGuid());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("abandoned", root.GetProperty("failureReason").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("holdExpiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("checkoutUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("refund").ValueKind);
        Assert.Equal("INR", root.GetProperty("currency").GetString());
        Assert.Equal(2, root.GetProperty("seats").GetArrayLength());
        Assert.Equal("Mumbai", root.GetProperty("trip").GetProperty("sourceCityName").GetString());
        Assert.Single(_seatInventory.Releases);
    }

    [Fact]
    public async Task ReleaseBooking_ConfirmedBooking_Returns409BookingNotUnpaidWithItsStatus()
    {
        // Arrange
        BookingSeedSpec booking = await SeedAsync(BookingStatus.Confirmed);
        using ApiFactory factory = CreateHost();
        using HttpClient client = factory.CreateClient();
        string token = TestAccessTokens.CreatePerson("Customer", "C-1", "priya@example.com");

        // Act
        using HttpResponseMessage response = await SendAsync(client, token, booking.BookingId);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BOOKING_NOT_UNPAID", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("confirmed", problem.RootElement.GetProperty("bookingStatus").GetString());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, Guid bookingId)
    {
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/bookings/{bookingId}/release", token);
        return await client.SendAsync(request);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private ApiFactory CreateHost()
    {
        return BookingTestHost.For(
            _database,
            BookingTestHost.Doubles(new FixedClock(_now), _seatInventory, new FakePaymentClient(), new FixedBookingSettings()));
    }

    private async Task<BookingSeedSpec> SeedAsync(BookingStatus status)
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        DateTimeOffset? confirmedAt = null;
        if (status == BookingStatus.Confirmed)
        {
            confirmedAt = _now.AddMinutes(-1);
        }

        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            Status = status,
            HoldExpiresAt = _now.AddMinutes(7),
            ConfirmedAt = confirmedAt,
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking;
    }
}
