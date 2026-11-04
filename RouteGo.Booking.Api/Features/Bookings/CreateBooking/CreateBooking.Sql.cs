namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

public sealed partial class CreateBookingHandler
{
    private const string SelectTripSql = """
        SELECT operator_id, is_cancelled, departure_at
        FROM trip_snapshots.trip_snapshots
        WHERE trip_id = @TripId
        """;

    private const string SelectSeatPricesSql = """
        SELECT seat_no, seat_class, fare
        FROM trip_snapshots.trip_snapshot_seats
        WHERE trip_id = @TripId AND seat_no = ANY(@SeatNos)
        """;

    // The partial unique index is what makes two requests from one customer unable to both pass: the
    // loser inserts nothing and the handler reads who won.
    private const string InsertBookingSql = """
        INSERT INTO bookings.bookings (booking_id, customer_id, contact_name, contact_email, trip_id, status, total_amount)
        VALUES (@BookingId, @CustomerId, @ContactName, @ContactEmail, @TripId, 'Initiated', @TotalAmount)
        ON CONFLICT (customer_id) WHERE status IN ('Initiated', 'SeatsHeld', 'PaymentPending') DO NOTHING
        """;

    private const string InsertSeatsSql = """
        INSERT INTO bookings.booking_seats (booking_id, seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender)
        SELECT @BookingId, seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender
        FROM unnest(@SeatNos, @SeatClasses, @Fares, @Names, @Ages, @Genders)
            AS seat(seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender)
        """;

    private const string SelectUnpaidBookingSql = """
        SELECT booking_id, hold_expires_at
        FROM bookings.bookings
        WHERE customer_id = @CustomerId AND status IN ('Initiated', 'SeatsHeld', 'PaymentPending')
        """;

    private const string MarkHeldSql = """
        UPDATE bookings.bookings SET status = 'SeatsHeld', hold_expires_at = @HoldExpiresAt
        WHERE booking_id = @BookingId AND status = 'Initiated'
        """;

    private const string MarkPaymentPendingSql = """
        UPDATE bookings.bookings SET status = 'PaymentPending', payment_id = @PaymentId, checkout_url = @CheckoutUrl
        WHERE booking_id = @BookingId AND status = 'SeatsHeld'
        """;

    private const string MarkFailedSql = """
        UPDATE bookings.bookings SET status = 'Failed', failure_reason = @Reason
        WHERE booking_id = @BookingId AND status = @From
        """;

    private const string SelectBookingSql = """
        SELECT b.booking_id, b.status, b.contact_name, b.contact_email, b.total_amount, b.hold_expires_at, b.checkout_url,
               b.failure_reason, b.payment_failure_detail, b.seat_change_count, b.created_at, b.confirmed_at,
               t.trip_id, t.source_city_name, t.destination_city_name, t.service_date, t.departure_at, t.arrival_at,
               t.bus_name, t.bus_type, t.operator_name
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        WHERE b.booking_id = @BookingId
        """;

    private const string SelectBookingSeatsSql = """
        SELECT seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender
        FROM bookings.booking_seats
        WHERE booking_id = @BookingId
        ORDER BY seat_no
        """;

    private sealed class TripRow
    {
        public string? OperatorId { get; init; }

        public bool IsCancelled { get; init; }

        public DateTime? DepartureAt { get; init; }
    }

    private sealed class SeatPriceRow
    {
        public string SeatNo { get; init; } = string.Empty;

        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
    }

    private sealed class UnpaidBookingRow
    {
        public Guid BookingId { get; init; }

        public DateTime? HoldExpiresAt { get; init; }
    }

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }

        public string Status { get; init; } = string.Empty;

        public string ContactName { get; init; } = string.Empty;

        public string ContactEmail { get; init; } = string.Empty;

        public long TotalAmount { get; init; }

        public DateTime? HoldExpiresAt { get; init; }

        public string? CheckoutUrl { get; init; }

        public string? FailureReason { get; init; }

        public string? PaymentFailureDetail { get; init; }

        public int SeatChangeCount { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? ConfirmedAt { get; init; }

        public Guid TripId { get; init; }

        public string SourceCityName { get; init; } = string.Empty;

        public string DestinationCityName { get; init; } = string.Empty;

        public DateTime ServiceDate { get; init; }

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }

        public string BusName { get; init; } = string.Empty;

        public string BusType { get; init; } = string.Empty;

        public string OperatorName { get; init; } = string.Empty;
    }

    private sealed class BookingSeatRow
    {
        public string SeatNo { get; init; } = string.Empty;

        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }

        public string PassengerName { get; init; } = string.Empty;

        public int PassengerAge { get; init; }

        public string PassengerGender { get; init; } = string.Empty;
    }
}
