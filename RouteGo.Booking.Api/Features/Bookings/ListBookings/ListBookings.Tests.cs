using FluentValidation.Results;

namespace RouteGo.Booking.Api.Features.Bookings.ListBookings;

[Collection(DatabaseCollection.Name)]
public sealed class ListBookingsTests
{
    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at, completed_at)
        VALUES (@BookingId, @PaymentId, @Reason, @Amount, @BookingId::text || CASE WHEN @Reason = 'Cancellation' THEN ':cancel' ELSE ':payment' END, @Status, now(),
                CASE WHEN @Status = 'Completed' THEN now() END)
        """;

    private const string ConfirmSql = """
        UPDATE bookings.bookings SET status = 'Confirmed', confirmed_at = now(), hold_expires_at = NULL
        WHERE booking_id = @BookingId
        """;

    private static readonly DateTimeOffset _confirmedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Caller _customer = new Caller("C-1", "priya@example.com", "Customer", null);

    private readonly DatabaseFixture _database;

    public ListBookingsTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_ConfirmedCancelledAndExpiredBookings_ListsNewestFirstWithRefundsTripSeatsAndTotal()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid confirmed = await SeedAsync(trip, "C-1", BookingStatus.Confirmed);
        Guid cancelled = await SeedAsync(trip, "C-1", BookingStatus.Cancelled);
        Guid expired = await SeedAsync(trip, "C-1", BookingStatus.Expired);
        await SeedRefundAsync(cancelled, "Cancellation", 989, "Completed");
        await SeedRefundAsync(expired, "LatePayment", 900, "Requested");

        // Act
        BookingPage page = await CreateHandler().HandleAsync(new ListBookingsRequest(), _customer, CancellationToken.None);

        // Assert
        Assert.Equal(new[] { expired, cancelled, confirmed }, page.Items.Select(item => item.BookingId).ToArray());
        Assert.Equal(3, page.TotalItems);
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        BookingSummary newest = page.Items[0];
        Assert.Equal(BookingStatus.Expired, newest.Status);
        Assert.Equal(new[] { "3A", "3B" }, newest.SeatNos);
        Assert.Equal(1600, newest.TotalAmount);
        Assert.Equal("INR", newest.Currency);
        Assert.Equal("Mumbai", newest.Trip.SourceCityName);
        Assert.Equal("Pune", newest.Trip.DestinationCityName);
        Assert.Equal(trip.DepartureAt, newest.Trip.DepartureAt);
        Assert.Equal(900, newest.Refund!.Amount);
        Assert.Equal(RefundStatus.Requested, newest.Refund.Status);
        Assert.Equal(RefundStatus.Completed, page.Items[1].Refund!.Status);
        Assert.Equal(989, page.Items[1].Refund!.Amount);
        Assert.Null(page.Items[2].Refund);
    }

    [Fact]
    public async Task HandleAsync_BookingStillBeingPaid_IsNotListedUntilItIsConfirmed()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid unpaid = await SeedAsync(trip, "C-1", BookingStatus.PaymentPending);
        var request = new ListBookingsRequest();

        // Act
        BookingPage beforePayment = await CreateHandler().HandleAsync(request, _customer, CancellationToken.None);
        await _database.SeedAsync(ConfirmSql, new { BookingId = unpaid });
        BookingPage afterPayment = await CreateHandler().HandleAsync(request, _customer, CancellationToken.None);

        // Assert
        Assert.Empty(beforePayment.Items);
        Assert.Equal(0, beforePayment.TotalItems);
        Assert.Equal(unpaid, Assert.Single(afterPayment.Items).BookingId);
    }

    [Theory]
    [InlineData(BookingStatus.Initiated)]
    [InlineData(BookingStatus.SeatsHeld)]
    public async Task HandleAsync_InitiatedOrSeatsHeldBooking_IsNotListed(BookingStatus status)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", status);

        // Act
        BookingPage page = await CreateHandler().HandleAsync(new ListBookingsRequest(), _customer, CancellationToken.None);

        // Assert
        Assert.Empty(page.Items);
    }

    [Theory]
    [InlineData(FailureReason.SeatsUnavailable)]
    [InlineData(FailureReason.InventoryUnavailable)]
    [InlineData(FailureReason.PaymentUnavailable)]
    public async Task HandleAsync_BookingThatFailedWhileBeingCreated_IsNotListed(FailureReason reason)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        await SeedAsync(trip, "C-1", BookingStatus.Failed, reason);

        // Act
        BookingPage page = await CreateHandler().HandleAsync(new ListBookingsRequest(), _customer, CancellationToken.None);

        // Assert
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);
    }

    [Theory]
    [InlineData(FailureReason.PaymentDeclined)]
    [InlineData(FailureReason.Abandoned)]
    [InlineData(FailureReason.TripCancelled)]
    [InlineData(FailureReason.SeatsLost)]
    public async Task HandleAsync_BookingThatFailedAfterReachingPayment_IsListed(FailureReason reason)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid failed = await SeedAsync(trip, "C-1", BookingStatus.Failed, reason);

        // Act
        BookingPage page = await CreateHandler().HandleAsync(new ListBookingsRequest(), _customer, CancellationToken.None);

        // Assert
        BookingSummary item = Assert.Single(page.Items);
        Assert.Equal(failed, item.BookingId);
        Assert.Equal(BookingStatus.Failed, item.Status);
    }

    [Fact]
    public async Task HandleAsync_AnotherCustomerHasBookings_ListsNoneOfThem()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        Guid mine = await SeedAsync(trip, "C-1", BookingStatus.Confirmed);
        await SeedAsync(trip, "C-2", BookingStatus.Confirmed);
        await SeedAsync(trip, "C-2", BookingStatus.Expired);

        // Act
        BookingPage page = await CreateHandler().HandleAsync(new ListBookingsRequest(), _customer, CancellationToken.None);

        // Assert
        Assert.Equal(mine, Assert.Single(page.Items).BookingId);
        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public async Task HandleAsync_FortyFiveBookingsPageThreeOfTwenty_ReturnsTheFiveOldestAndTheTotal()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        IReadOnlyList<Guid> seeded = await SeedManyAsync(trip, 45);
        var request = new ListBookingsRequest { Page = 3, PageSize = 20 };

        // Act
        BookingPage page = await CreateHandler().HandleAsync(request, _customer, CancellationToken.None);

        // Assert
        Guid[] oldestFiveNewestFirst = seeded.Take(5).Reverse().ToArray();
        Assert.Equal(oldestFiveNewestFirst, page.Items.Select(item => item.BookingId).ToArray());
        Assert.Equal(45, page.TotalItems);
        Assert.Equal(3, page.Page);
        Assert.Equal(20, page.PageSize);
    }

    [Fact]
    public async Task HandleAsync_CustomerWhoNeverBooked_ReturnsAnEmptyPageNotAnError()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var newcomer = new Caller("C-3", "new@example.com", "Customer", null);

        // Act
        BookingPage page = await CreateHandler().HandleAsync(new ListBookingsRequest(), newcomer, CancellationToken.None);

        // Assert
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 201)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void Validate_PageOrPageSizeOutOfRange_IsRefused(int? page, int? pageSize)
    {
        // Arrange
        var validator = new ListBookingsValidator();
        var request = new ListBookingsRequest { Page = page, PageSize = pageSize };

        // Act
        ValidationResult result = validator.Validate(request);

        // Assert
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, 1)]
    [InlineData(5, 200)]
    public void Validate_PageAndPageSizeInRange_IsAccepted(int? page, int? pageSize)
    {
        // Arrange
        var validator = new ListBookingsValidator();
        var request = new ListBookingsRequest { Page = page, PageSize = pageSize };

        // Act
        ValidationResult result = validator.Validate(request);

        // Assert
        Assert.True(result.IsValid);
    }

    private ListBookingsHandler CreateHandler()
    {
        return new ListBookingsHandler(TestWriters.CreateReader(_database));
    }

    // Oldest first: each booking is stamped created when it is inserted, so the last one is the newest.
    private async Task<IReadOnlyList<Guid>> SeedManyAsync(TripSeed trip, int count)
    {
        var seeded = new List<Guid>();
        for (int index = 0; index < count; index++)
        {
            seeded.Add(await SeedAsync(trip, "C-1", BookingStatus.Confirmed));
        }

        return seeded;
    }

    private async Task<Guid> SeedAsync(TripSeed trip, string customerId, BookingStatus status, FailureReason? reason = null)
    {
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = customerId,
            Status = status,
            FailureReason = reason,
            ConfirmedAt = _confirmedAt,
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking.BookingId;
    }

    private async Task SeedRefundAsync(Guid bookingId, string reason, long amount, string status)
    {
        var parameters = new { BookingId = bookingId, PaymentId = Guid.NewGuid(), Reason = reason, Amount = amount, Status = status };
        await _database.SeedAsync(InsertRefundSql, parameters);
    }
}
