using System.Text.Json;

namespace RouteGo.Booking.Api.Features.Bookings.GetBooking;

[Collection(DatabaseCollection.Name)]
public sealed class GetBookingTests
{
    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at, completed_at)
        VALUES (@BookingId, @PaymentId, @Reason, @Amount, @BookingId::text || @KeySuffix, @Status, @RequestedAt, @CompletedAt)
        """;

    private const string InsertCancellationSql = """
        INSERT INTO cancellations.cancellations (booking_id, kind, reason, refund_percent, refund_amount, cancelled_by, cancelled_at)
        VALUES (@BookingId, 'CustomerCancelled', NULL, 90, 989, 'C-1', @CancelledAt)
        """;

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly Caller _owner = new Caller("C-1", "priya@example.com", "Customer", null);

    private readonly DatabaseFixture _database;

    public GetBookingTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_PaymentPendingBookingWithinHold_ShowsHoldExpiryCheckoutUrlTripSeatsAndTotal()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        DateTimeOffset holdExpiresAt = _now.AddMinutes(6);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            HoldExpiresAt = holdExpiresAt,
            CheckoutUrl = "http://pay.example/checkout/77",
        };
        await BookingSeed.SeedBookingAsync(_database, booking);

        // Act
        GetBookingResult result = await CreateHandler().HandleAsync(booking.BookingId, _owner, CancellationToken.None);

        // Assert
        Assert.Equal(GetBookingOutcome.Found, result.Outcome);
        Booking found = result.Booking!;
        Assert.Equal(BookingStatus.PaymentPending, found.Status);
        Assert.Equal(holdExpiresAt, found.HoldExpiresAt);
        Assert.Equal("http://pay.example/checkout/77", found.CheckoutUrl);
        Assert.Equal(1600, found.TotalAmount);
        Assert.Equal("INR", found.Currency);
        Assert.Equal("Priya Shah", found.ContactName);
        Assert.Equal("priya@example.com", found.ContactEmail);
        Assert.Equal(new[] { "3A", "3B" }, found.Seats.Select(seat => seat.SeatNo).ToArray());
        Assert.Equal(new[] { "Priya Shah", "Ravi Shah" }, found.Seats.Select(seat => seat.PassengerName).ToArray());
        Assert.All(found.Seats, seat => Assert.Equal(800, seat.Fare));
        Assert.Equal("Sleeper", found.Seats[0].SeatClass);
        Assert.Equal(trip.TripId, found.Trip.TripId);
        Assert.Equal("Mumbai", found.Trip.SourceCityName);
        Assert.Equal("Pune", found.Trip.DestinationCityName);
        Assert.Equal(BusType.Sleeper, found.Trip.BusType);
        Assert.Equal("Sharma Travels", found.Trip.OperatorName);
        Assert.Equal(trip.DepartureAt, found.Trip.DepartureAt);
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBooking_ShowsConfirmedAtAndNoHoldExpiryNorCheckoutUrl()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        DateTimeOffset confirmedAt = _now.AddMinutes(-5);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            Status = BookingStatus.Confirmed,
            ConfirmedAt = confirmedAt,
            CheckoutUrl = "http://pay.example/checkout/77",
        };
        await BookingSeed.SeedBookingAsync(_database, booking);

        // Act
        GetBookingResult result = await CreateHandler().HandleAsync(booking.BookingId, _owner, CancellationToken.None);

        // Assert
        Booking found = result.Booking!;
        Assert.Equal(BookingStatus.Confirmed, found.Status);
        Assert.Equal(confirmedAt, found.ConfirmedAt);
        Assert.Null(found.HoldExpiresAt);
        Assert.Null(found.CheckoutUrl);
        Assert.Equal(2, found.Seats.Count);
        Assert.Null(found.Cancellation);
        Assert.Null(found.Refund);
    }

    [Fact]
    public async Task HandleAsync_PaymentPendingBookingWhoseHoldRanOut_ShowsNoCheckoutUrl()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            HoldExpiresAt = _now.AddMinutes(-1),
            CheckoutUrl = "http://pay.example/checkout/77",
        };
        await BookingSeed.SeedBookingAsync(_database, booking);

        // Act
        GetBookingResult result = await CreateHandler().HandleAsync(booking.BookingId, _owner, CancellationToken.None);

        // Assert
        Booking found = result.Booking!;
        Assert.Equal(BookingStatus.PaymentPending, found.Status);
        Assert.Null(found.CheckoutUrl);
        Assert.Equal(_now.AddMinutes(-1), found.HoldExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_FailedBookingAndExpiredBookingWithLateRefund_ShowFailureReasonAndRefund()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var failed = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = "C-2",
            Status = BookingStatus.Failed,
            FailureReason = FailureReason.PaymentDeclined,
        };
        var expired = new BookingSeedSpec
        {
            TripId = trip.TripId,
            Status = BookingStatus.Expired,
            Passengers = new List<SeedPassenger> { new SeedPassenger("4C", "Sameer Rao", 900) },
        };
        await BookingSeed.SeedBookingAsync(_database, failed);
        await BookingSeed.SeedBookingAsync(_database, expired);
        await SeedRefundAsync(expired.BookingId, new RefundSeed { Reason = "LatePayment", Amount = 900, Status = "Completed", KeySuffix = ":payment", CompletedAt = _now.UtcDateTime });
        var otherCustomer = new Caller("C-2", "other@example.com", "Customer", null);

        // Act
        GetBookingResult failedResult = await CreateHandler().HandleAsync(failed.BookingId, otherCustomer, CancellationToken.None);
        GetBookingResult expiredResult = await CreateHandler().HandleAsync(expired.BookingId, _owner, CancellationToken.None);

        // Assert
        Assert.Equal(FailureReason.PaymentDeclined, failedResult.Booking!.FailureReason);
        Assert.Null(failedResult.Booking.Refund);
        Booking expiredBooking = expiredResult.Booking!;
        Assert.Equal(BookingStatus.Expired, expiredBooking.Status);
        Assert.Null(expiredBooking.FailureReason);
        Assert.Equal(RefundReason.LatePayment, expiredBooking.Refund!.Reason);
        Assert.Equal(900, expiredBooking.Refund.Amount);
        Assert.Equal(RefundStatus.Completed, expiredBooking.Refund.Status);
        Assert.NotNull(expiredBooking.Refund.CompletedAt);
    }

    [Fact]
    public async Task HandleAsync_CancelledBookingWithRequestedRefund_ShowsCancellationAndRefundWithoutCompletion()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            Status = BookingStatus.Cancelled,
            ConfirmedAt = _now.AddDays(-2),
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        await _database.SeedAsync(InsertCancellationSql, new { booking.BookingId, CancelledAt = _now.AddHours(-1).UtcDateTime });
        await SeedRefundAsync(booking.BookingId, new RefundSeed { Reason = "Cancellation", Amount = 989, Status = "Requested", KeySuffix = ":cancel" });

        // Act
        GetBookingResult result = await CreateHandler().HandleAsync(booking.BookingId, _owner, CancellationToken.None);

        // Assert
        Booking found = result.Booking!;
        Assert.Equal(BookingStatus.Cancelled, found.Status);
        Assert.Equal(CancellationKind.CustomerCancelled, found.Cancellation!.Kind);
        Assert.Null(found.Cancellation.Reason);
        Assert.Equal(90, found.Cancellation.RefundPercent);
        Assert.Equal(989, found.Cancellation.RefundAmount);
        Assert.Equal(_now.AddHours(-1), found.Cancellation.CancelledAt);
        Assert.Equal(RefundStatus.Requested, found.Refund!.Status);
        Assert.Equal(RefundReason.Cancellation, found.Refund.Reason);
        Assert.Null(found.Refund.CompletedAt);
    }

    [Fact]
    public async Task HandleAsync_AnotherCustomersBooking_IsForbiddenAndShowsNothing()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec { TripId = trip.TripId };
        await BookingSeed.SeedBookingAsync(_database, booking);
        var stranger = new Caller("C-2", "other@example.com", "Customer", null);

        // Act
        GetBookingResult result = await CreateHandler().HandleAsync(booking.BookingId, stranger, CancellationToken.None);

        // Assert
        Assert.Equal(GetBookingOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Booking);
    }

    [Fact]
    public async Task HandleAsync_AdminReadsACustomersBooking_SeesTheSameAnswerTheCustomerSees()
    {
        // Arrange
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
        var admin = new Caller("A-1", "admin@routego.in", "Admin", null);

        // Act
        GetBookingResult forAdmin = await CreateHandler().HandleAsync(booking.BookingId, admin, CancellationToken.None);
        GetBookingResult forCustomer = await CreateHandler().HandleAsync(booking.BookingId, _owner, CancellationToken.None);

        // Assert
        Assert.Equal(GetBookingOutcome.Found, forAdmin.Outcome);
        string adminJson = JsonSerializer.Serialize(forAdmin.Booking, WireJson.Options);
        string customerJson = JsonSerializer.Serialize(forCustomer.Booking, WireJson.Options);
        Assert.Equal(customerJson, adminJson);
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsNotFoundForACustomerAndForAnAdmin()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var admin = new Caller("A-1", "admin@routego.in", "Admin", null);

        // Act
        GetBookingResult forCustomer = await CreateHandler().HandleAsync(Guid.NewGuid(), _owner, CancellationToken.None);
        GetBookingResult forAdmin = await CreateHandler().HandleAsync(Guid.NewGuid(), admin, CancellationToken.None);

        // Assert
        Assert.Equal(GetBookingOutcome.NotFound, forCustomer.Outcome);
        Assert.Equal(GetBookingOutcome.NotFound, forAdmin.Outcome);
    }

    private GetBookingHandler CreateHandler()
    {
        return new GetBookingHandler(TestWriters.CreateReader(_database), new FixedClock(_now));
    }

    private async Task SeedRefundAsync(Guid bookingId, RefundSeed refund)
    {
        var parameters = new
        {
            BookingId = bookingId,
            PaymentId = Guid.NewGuid(),
            refund.Reason,
            refund.Amount,
            refund.KeySuffix,
            refund.Status,
            RequestedAt = _now.AddMinutes(-30).UtcDateTime,
            refund.CompletedAt,
        };
        await _database.SeedAsync(InsertRefundSql, parameters);
    }

    private sealed class RefundSeed
    {
        public required string Reason { get; init; }

        public required long Amount { get; init; }

        public required string Status { get; init; }

        public required string KeySuffix { get; init; }

        public DateTime? CompletedAt { get; init; }
    }
}
