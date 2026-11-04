namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

/// <summary>A booking row as the database holds it, for assertions.</summary>
internal sealed class BookingStateRow
{
    public Guid BookingId { get; init; }

    public string CustomerId { get; init; } = string.Empty;

    public string ContactName { get; init; } = string.Empty;

    public string ContactEmail { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public long TotalAmount { get; init; }

    public DateTime? HoldExpiresAt { get; init; }

    public Guid? PaymentId { get; init; }

    public string? CheckoutUrl { get; init; }

    public string? FailureReason { get; init; }
}

/// <summary>A recorded event row, for assertions.</summary>
internal sealed class RecordedEventRow
{
    public string EventType { get; init; } = string.Empty;

    public string Stream { get; init; } = string.Empty;

    public string EventData { get; init; } = string.Empty;

    public string CorrelationId { get; init; } = string.Empty;

    public DateTime? SentAt { get; init; }
}

/// <summary>The doubles, clock and handler a booking test drives, plus the queries that read back what it left behind.</summary>
internal sealed class CreateBookingScenario
{
    public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 16, 12, 30, 0, TimeSpan.Zero);

    public const string CustomerEmail = "priya@example.com";

    private const string BookingsSql = """
        SELECT booking_id, customer_id, contact_name, contact_email, status, total_amount, hold_expires_at,
               payment_id, checkout_url, failure_reason
        FROM bookings.bookings ORDER BY created_at
        """;

    private const string EventsSql = """
        SELECT event_type, stream, event_data, correlation_id, sent_at FROM bookings.booking_events ORDER BY recorded_at
        """;

    private readonly DatabaseFixture _database;

    public CreateBookingScenario(DatabaseFixture database, DateTimeOffset now)
    {
        _database = database;
        Clock = new FixedClock(now);
        SeatInventory.HoldResult = new HoldSeatsResult
        {
            Outcome = HoldSeatsOutcome.Held,
            HoldExpiresAt = now.AddSeconds(Settings.Current.HoldSeconds),
        };
    }

    public FixedClock Clock { get; }

    public FakeSeatInventoryClient SeatInventory { get; } = new FakeSeatInventoryClient();

    public FakePaymentClient Payment { get; } = new FakePaymentClient();

    public FixedBookingSettings Settings { get; } = new FixedBookingSettings();

    public CreateBookingHandler CreateHandler(ISeatInventoryClient seatInventory)
    {
        return new CreateBookingHandler(TestWriters.CreateWriter(_database), seatInventory, Payment, Clock);
    }

    public Task<CreateBookingResult> BookAsync(Guid tripId, long expectedTotal, params string[] seatNos)
    {
        return BookAsAsync("C-1", CustomerEmail, Request(tripId, expectedTotal, seatNos));
    }

    public Task<CreateBookingResult> BookAsAsync(string customerId, string email, BookingRequest request)
    {
        var caller = new Caller(customerId, email, "Customer", null);
        var order = new BookingOrder(caller, request, "corr-create");
        return CreateHandler(SeatInventory).HandleAsync(order, Settings.Current, CancellationToken.None);
    }

    public static BookingRequest Request(Guid tripId, long expectedTotal, params string[] seatNos)
    {
        var seats = new List<BookingSeatRequest>();
        foreach (string seatNo in seatNos)
        {
            seats.Add(new BookingSeatRequest
            {
                SeatNo = seatNo,
                PassengerName = $"Passenger {seatNo}",
                PassengerAge = 34,
                PassengerGender = "Female",
            });
        }

        return new BookingRequest { TripId = tripId, ContactName = "Priya Shah", Seats = seats, ExpectedTotal = expectedTotal };
    }

    public Task<IReadOnlyList<BookingStateRow>> GetBookingsAsync()
    {
        return TestQueries.QueryAsync<BookingStateRow>(_database, BookingsSql);
    }

    public Task<IReadOnlyList<RecordedEventRow>> GetEventsAsync()
    {
        return TestQueries.QueryAsync<RecordedEventRow>(_database, EventsSql);
    }
}
