namespace RouteGo.Booking.Api.Tests.Support;

/// <summary>
/// Puts trip snapshots and bookings into the test database the way the workers and handlers would
/// leave them, so a test starts from a known state without driving the whole flow.
/// </summary>
public static class BookingSeed
{
    private const string InsertTripSql = """
        INSERT INTO trip_snapshots.trip_snapshots
            (trip_id, operator_id, operator_name, operator_email, source_city_name, destination_city_name, service_date,
             departure_at, arrival_at, bus_name, bus_type, is_ac, is_cancelled, cancellation_reason, cancelled_at)
        VALUES
            (@TripId, @OperatorId, @OperatorName, @OperatorEmail, @Source, @Destination, (@DepartureAt AT TIME ZONE 'Asia/Kolkata')::date,
             @DepartureAt, @DepartureAt + interval '3 hours', 'Volvo 9600', 'Sleeper', true, @IsCancelled,
             CASE WHEN @IsCancelled THEN 'Bus breakdown' END,
             CASE WHEN @IsCancelled THEN @DepartureAt - interval '1 day' END)
        """;

    private const string InsertTripSeatSql = """
        INSERT INTO trip_snapshots.trip_snapshot_seats (trip_id, seat_no, seat_class, fare)
        VALUES (@TripId, @SeatNo, @SeatClass, @Fare)
        """;

    private const string InsertBookingSql = """
        INSERT INTO bookings.bookings
            (booking_id, customer_id, contact_name, contact_email, trip_id, status, total_amount, hold_expires_at, payment_id,
             checkout_url, failure_reason, seat_change_count, confirmed_at)
        VALUES
            (@BookingId, @CustomerId, @ContactName, @ContactEmail, @TripId, @Status, @TotalAmount, @HoldExpiresAt, @PaymentId,
             @CheckoutUrl, @FailureReason, @SeatChangeCount, @ConfirmedAt)
        """;

    private const string InsertBookingSeatSql = """
        INSERT INTO bookings.booking_seats
            (booking_id, seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender)
        SELECT @BookingId, @SeatNo, s.seat_class, @Fare, @PassengerName, 30, 'Female'
        FROM trip_snapshots.trip_snapshot_seats s
        WHERE s.trip_id = @TripId AND s.seat_no = @SeatNo
        """;

    /// <summary>Inserts the trip snapshot and its seats.</summary>
    public static async Task SeedTripAsync(DatabaseFixture database, TripSeed trip)
    {
        var tripParameters = new
        {
            trip.TripId,
            trip.OperatorId,
            trip.OperatorName,
            trip.OperatorEmail,
            trip.Source,
            trip.Destination,
            DepartureAt = trip.DepartureAt.UtcDateTime,
            trip.IsCancelled,
        };
        await database.SeedAsync(InsertTripSql, tripParameters, "test:seed-trip");
        foreach (SeedSeat seat in trip.Seats)
        {
            var seatParameters = new { trip.TripId, seat.SeatNo, seat.SeatClass, seat.Fare };
            await database.SeedAsync(InsertTripSeatSql, seatParameters, "test:seed-trip-seat");
        }
    }

    /// <summary>Inserts the booking and its seats; the trip must already be seeded, with a snapshot seat for each passenger.</summary>
    public static async Task SeedBookingAsync(DatabaseFixture database, BookingSeedSpec booking)
    {
        long total = booking.Passengers.Sum(passenger => passenger.Fare);
        string? failureReason = null;
        if (booking.FailureReason is not null)
        {
            failureReason = booking.FailureReason.Value.ToString();
        }

        var bookingParameters = new
        {
            booking.BookingId,
            booking.CustomerId,
            booking.ContactName,
            booking.ContactEmail,
            booking.TripId,
            Status = booking.Status.ToString(),
            TotalAmount = total,
            HoldExpiresAt = booking.HoldExpiresAt?.UtcDateTime,
            booking.PaymentId,
            booking.CheckoutUrl,
            FailureReason = failureReason,
            booking.SeatChangeCount,
            ConfirmedAt = booking.ConfirmedAt?.UtcDateTime,
        };
        await database.SeedAsync(InsertBookingSql, bookingParameters, "test:seed-booking");
        foreach (SeedPassenger passenger in booking.Passengers)
        {
            var seatParameters = new
            {
                booking.BookingId,
                booking.TripId,
                passenger.SeatNo,
                passenger.Fare,
                passenger.PassengerName,
            };
            await database.SeedAsync(InsertBookingSeatSql, seatParameters, "test:seed-booking-seat");
        }
    }
}
