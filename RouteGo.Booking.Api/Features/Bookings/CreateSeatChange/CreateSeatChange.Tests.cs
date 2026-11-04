using System.Text.Json;

namespace RouteGo.Booking.Api.Features.Bookings.CreateSeatChange;

[Collection(DatabaseCollection.Name)]
public sealed class CreateSeatChangeTests
{
    // 2026-10-25 at 12:00 UTC; the trip leaves five hours later.
    internal static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    internal const string CustomerId = "C-1";

    private const string CorrelationId = "seat-change-test";

    private const string SeatsSql = """
        SELECT string_agg(seat_no || ':' || passenger_name || ':' || fare || ':' || seat_class, ',' ORDER BY seat_no)
        FROM bookings.booking_seats WHERE booking_id = @BookingId
        """;

    private const string CountSql = "SELECT seat_change_count FROM bookings.bookings WHERE booking_id = @BookingId";

    private const string TotalSql = "SELECT total_amount FROM bookings.bookings WHERE booking_id = @BookingId";

    private const string EventTypesSql = """
        SELECT coalesce(string_agg(event_type, ',' ORDER BY recorded_at), '-')
        FROM bookings.booking_events WHERE booking_id = @BookingId
        """;

    private const string EventDataSql = """
        SELECT event_data FROM bookings.booking_events WHERE booking_id = @BookingId AND event_type = 'BookingSeatsChanged'
        """;

    private const string SetTripCancelledSql = """
        UPDATE trip_snapshots.trip_snapshots
        SET is_cancelled = true, cancelled_at = @CancelledAt, cancellation_reason = 'Bus breakdown'
        WHERE trip_id = @TripId
        """;

    private readonly DatabaseFixture _database;

    public CreateSeatChangeTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_MovingOneSeat_MovesThePassengerAndFareCountsOneChangeAndKeepsTheTotal()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.Changed, result.Outcome);
        Assert.Equal("4D:Amit Rao:600:Seater,5C:Sameer Rao:600:Seater", await SeatsAsync(bookingId));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(CountSql, new { BookingId = bookingId }));
        Assert.Equal(1200, await _database.QueryScalarAsync<long>(TotalSql, new { BookingId = bookingId }));
        SeatChange response = result.Response!;
        Assert.Equal(1, response.SeatChangeCount);
        Assert.Equal(new SeatMove("4C", "5C"), response.Changes.Single());
        Assert.Equal("4D,5C", string.Join(',', response.Seats.Select(seat => seat.SeatNo)));
        Assert.Equal(PassengerGender.Female, response.Seats[1].PassengerGender);
        FakeChangeCall call = seatInventory.Changes.Single();
        Assert.Equal(bookingId, call.BookingId);
        Assert.Equal(new SeatMove("4C", "5C"), call.Moves.Single());
    }

    [Fact]
    public async Task HandleAsync_MovingOneSeat_RecordsBookingSeatsChangedWithTheTicketAfterTheChangeAndTheMove()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);

        // Act
        await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal("BookingSeatsChanged", await EventTypesAsync(bookingId));
        string data = (await _database.QueryScalarAsync<string>(EventDataSql, new { BookingId = bookingId }))!;
        using var document = JsonDocument.Parse(data);
        JsonElement root = document.RootElement;
        Assert.Equal("4C", root.GetProperty("changes")[0].GetProperty("fromSeatNo").GetString());
        Assert.Equal("5C", root.GetProperty("changes")[0].GetProperty("toSeatNo").GetString());
        Assert.Equal(1, root.GetProperty("seatChangeCount").GetInt32());
        Assert.Equal(1200, root.GetProperty("totalAmount").GetInt64());
        Assert.Equal("5C", root.GetProperty("seats")[1].GetProperty("seatNo").GetString());
        Assert.Equal("Sameer Rao", root.GetProperty("seats")[1].GetProperty("name").GetString());
        Assert.Equal("Mumbai", root.GetProperty("trip").GetProperty("source").GetString());
    }

    [Fact]
    public async Task HandleAsync_SwappingTwoSeats_SwapsThePassengersAndKeepsEachFareAndClass()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);
        List<SeatMove> swap = Moves(Move("4C", "4D"), Move("4D", "4C"));

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), swap, CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.Changed, result.Outcome);
        Assert.Equal("4C:Amit Rao:600:Seater,4D:Sameer Rao:600:Seater", await SeatsAsync(bookingId));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(CountSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryNamesAnUnavailableSeat_IsRefusedNamingItAndNothingChanges()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        seatInventory.ChangeResult = new ChangeSeatsResult
        {
            Outcome = ChangeSeatsOutcome.SeatsUnavailable,
            NamedSeats = new List<string> { "5D" },
        };
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(
            Target(bookingId), Moves(Move("4C", "5C"), Move("4D", "5D")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.SeatsUnavailable, result.Outcome);
        Assert.Equal("5D", result.SeatNos.Single());
        Assert.Equal("4C:Sameer Rao:600:Seater,4D:Amit Rao:600:Seater", await SeatsAsync(bookingId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(CountSql, new { BookingId = bookingId }));
        Assert.Equal("-", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_MoveToASeatOfAnotherClass_IsRefusedNamingTheMoveWithoutAskingSeatInventory()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "L3")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.SeatClassMismatch, result.Outcome);
        Assert.Equal(new SeatMove("4C", "L3"), result.Moves.Single());
        Assert.Empty(seatInventory.Changes);
        Assert.Equal("4C:Sameer Rao:600:Seater,4D:Amit Rao:600:Seater", await SeatsAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_MoveFromASeatThatIsNotTheBookings_IsRefusedNamingIt()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("6A", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.SeatsNotInBooking, result.Outcome);
        Assert.Equal("6A", result.SeatNos.Single());
        Assert.Empty(seatInventory.Changes);
    }

    [Fact]
    public async Task HandleAsync_MoveToASeatTheTripDoesNotHave_IsRefusedNamingIt()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "Z99")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.UnknownSeats, result.Outcome);
        Assert.Equal("Z99", result.SeatNos.Single());
        Assert.Empty(seatInventory.Changes);
    }

    [Fact]
    public async Task HandleAsync_ThirtyMinutesBeforeDeparture_IsStillAccepted()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromMinutes(30), 0);
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.Changed, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_TwentyNineMinutesBeforeDeparture_IsRefusedWithTheDepartureAndTheCutoff()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromMinutes(29), 0);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.BookingClosed, result.Outcome);
        Assert.Equal(Now.AddMinutes(29), result.DepartureAt);
        Assert.Equal(30, result.CutoffMinutes);
        Assert.Empty(seatInventory.Changes);
    }

    [Fact]
    public async Task HandleAsync_AfterTheThirdSeatChange_IsRefusedWithTheLimitAndNothingChanges()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 3);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.SeatChangeLimitReached, result.Outcome);
        Assert.Equal(3, result.MaxSeatChanges);
        Assert.Empty(seatInventory.Changes);
        Assert.Equal(3, await _database.QueryScalarAsync<int>(CountSql, new { BookingId = bookingId }));
    }

    [Fact]
    public async Task HandleAsync_LimitReachedAndASeatNotTheBookings_RefusesTheLimitFirst()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 3);
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("6A", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.SeatChangeLimitReached, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_TheSameChangeAgain_SucceedsWithNoMovesNoCountAndNoEvent()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 1);
        var seatInventory = new FakeSeatInventoryClient();
        await MoveBookingSeatAsync(bookingId, "4C", "5C");
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.AlreadyDone, result.Outcome);
        Assert.Empty(result.Response!.Changes);
        Assert.Equal(1, result.Response!.SeatChangeCount);
        Assert.Equal("4D,5C", string.Join(',', result.Response!.Seats.Select(seat => seat.SeatNo)));
        Assert.Equal(1, await _database.QueryScalarAsync<int>(CountSql, new { BookingId = bookingId }));
        Assert.Equal("-", await EventTypesAsync(bookingId));
        Assert.Empty(seatInventory.Changes);
    }

    [Fact]
    public async Task HandleAsync_TheSameChangeAgainAtTheLimit_StillSucceeds()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 3);
        await MoveBookingSeatAsync(bookingId, "4C", "5C");
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.AlreadyDone, result.Outcome);
        Assert.Equal(3, result.Response!.SeatChangeCount);
    }

    [Theory]
    [InlineData(BookingStatus.PaymentPending)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task HandleAsync_BookingThatIsNotConfirmed_IsRefusedNamingItsStatus(BookingStatus status)
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, status, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.BookingNotConfirmed, result.Outcome);
        Assert.Equal(status, result.BookingStatus);
        Assert.Empty(seatInventory.Changes);
    }

    [Fact]
    public async Task HandleAsync_TripCancelledInTheSnapshot_IsRefusedTripCancelled()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        Guid tripId = await _database.QueryScalarAsync<Guid>("SELECT trip_id FROM bookings.bookings WHERE booking_id = @BookingId", new { BookingId = bookingId });
        await _database.SeedAsync(SetTripCancelledSql, new { TripId = tripId, CancelledAt = Now.UtcDateTime });
        var seatInventory = new FakeSeatInventoryClient();
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.TripCancelled, result.Outcome);
        Assert.Empty(seatInventory.Changes);
    }

    [Fact]
    public async Task HandleAsync_AnotherCustomersBooking_IsRefusedAsNotTheirs()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);
        var stranger = new SeatChangeTarget(bookingId, new Caller("C-2", "x@example.com", "Customer", null), CorrelationId);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(stranger, Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.NotTheCustomersBooking, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_UnknownBooking_IsRefusedNotFound()
    {
        // Arrange
        CreateSeatChangeHandler handler = CreateHandler(new FakeSeatInventoryClient(), new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(Guid.NewGuid()), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.NotFound, result.Outcome);
    }

    [Theory]
    [InlineData(ChangeSeatsOutcome.Unavailable, CreateSeatChangeOutcome.UpstreamUnavailable)]
    [InlineData(ChangeSeatsOutcome.TimedOut, CreateSeatChangeOutcome.UpstreamTimeout)]
    [InlineData(ChangeSeatsOutcome.TripCancelled, CreateSeatChangeOutcome.TripCancelled)]
    [InlineData(ChangeSeatsOutcome.SeatsNotOwned, CreateSeatChangeOutcome.SeatsNotInBooking)]
    [InlineData(ChangeSeatsOutcome.UnknownSeats, CreateSeatChangeOutcome.UnknownSeats)]
    [InlineData(ChangeSeatsOutcome.SeatClassMismatch, CreateSeatChangeOutcome.SeatClassMismatch)]
    [InlineData(ChangeSeatsOutcome.Refused, CreateSeatChangeOutcome.UpstreamUnavailable)]
    public async Task HandleAsync_SeatInventoryDoesNotMoveTheSeats_BookingIsUntouchedAndTheRefusalIsMapped(
        ChangeSeatsOutcome answer, CreateSeatChangeOutcome expected)
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        seatInventory.ChangeResult = new ChangeSeatsResult { Outcome = answer };
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(expected, result.Outcome);
        Assert.Equal("4C:Sameer Rao:600:Seater,4D:Amit Rao:600:Seater", await SeatsAsync(bookingId));
        Assert.Equal(0, await _database.QueryScalarAsync<int>(CountSql, new { BookingId = bookingId }));
        Assert.Equal("-", await EventTypesAsync(bookingId));
    }

    [Fact]
    public async Task HandleAsync_SeatInventoryDown_IsRetryableAndTimeoutIsNamedApart()
    {
        // Arrange
        Guid bookingId = await SeedAsync(_database, BookingStatus.Confirmed, TimeSpan.FromHours(5), 0);
        var seatInventory = new FakeSeatInventoryClient();
        seatInventory.ChangeResult = new ChangeSeatsResult { Outcome = ChangeSeatsOutcome.Unavailable };
        CreateSeatChangeHandler handler = CreateHandler(seatInventory, new FixedBookingSettings(), Now);

        // Act
        CreateSeatChangeResult result = await handler.HandleAsync(Target(bookingId), Moves(Move("4C", "5C")), CancellationToken.None);

        // Assert
        Assert.Equal(CreateSeatChangeOutcome.UpstreamUnavailable, result.Outcome);
        Assert.True(result.IsRetryable);
    }

    internal static async Task<Guid> SeedAsync(
        DatabaseFixture database, BookingStatus status, TimeSpan untilDeparture, int seatChangeCount)
    {
        await database.ResetBusinessTablesAsync();
        var seats = new List<SeedSeat>
        {
            new SeedSeat("4C", "Seater", 600),
            new SeedSeat("4D", "Seater", 600),
            new SeedSeat("5C", "Seater", 600),
            new SeedSeat("5D", "Seater", 600),
            new SeedSeat("L3", "Sleeper", 900),
        };
        var trip = new TripSeed { DepartureAt = Now + untilDeparture, Seats = seats };
        await BookingSeed.SeedTripAsync(database, trip);
        var booking = new BookingSeedSpec
        {
            TripId = trip.TripId,
            CustomerId = CustomerId,
            Status = status,
            PaymentId = Guid.NewGuid(),
            ConfirmedAt = Now.AddDays(-1),
            SeatChangeCount = seatChangeCount,
            Passengers = new List<SeedPassenger>
            {
                new SeedPassenger("4C", "Sameer Rao", 600),
                new SeedPassenger("4D", "Amit Rao", 600),
            },
        };
        await BookingSeed.SeedBookingAsync(database, booking);
        return booking.BookingId;
    }

    internal static SeatMove Move(string from, string to)
    {
        return new SeatMove(from, to);
    }

    internal static List<SeatMove> Moves(params SeatMove[] moves)
    {
        return moves.ToList();
    }

    internal static SeatChangeTarget Target(Guid bookingId)
    {
        var caller = new Caller(CustomerId, "priya@example.com", "Customer", null);
        return new SeatChangeTarget(bookingId, caller, CorrelationId);
    }

    internal static CreateSeatChangeHandler CreateHandlerFor(
        DatabaseFixture database, FakeSeatInventoryClient seatInventory, FixedBookingSettings settings, DateTimeOffset now)
    {
        IWriterConnectionFactory writerFactory = TestWriters.CreateWriter(database);
        return new CreateSeatChangeHandler(writerFactory, seatInventory, settings, new FixedClock(now));
    }

    private CreateSeatChangeHandler CreateHandler(FakeSeatInventoryClient seatInventory, FixedBookingSettings settings, DateTimeOffset now)
    {
        return CreateHandlerFor(_database, seatInventory, settings, now);
    }

    private async Task<string> SeatsAsync(Guid bookingId)
    {
        return (await _database.QueryScalarAsync<string>(SeatsSql, new { BookingId = bookingId }))!;
    }

    private async Task<string> EventTypesAsync(Guid bookingId)
    {
        return (await _database.QueryScalarAsync<string>(EventTypesSql, new { BookingId = bookingId }))!;
    }

    // Puts the booking where a finished change would have left it: the passenger on the new seat.
    private async Task MoveBookingSeatAsync(Guid bookingId, string from, string to)
    {
        const string sql = "UPDATE bookings.booking_seats SET seat_no = @To WHERE booking_id = @BookingId AND seat_no = @From";
        await _database.SeedAsync(sql, new { BookingId = bookingId, From = from, To = to });
    }
}
