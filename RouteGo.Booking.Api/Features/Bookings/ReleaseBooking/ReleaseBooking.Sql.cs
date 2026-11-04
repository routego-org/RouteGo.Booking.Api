namespace RouteGo.Booking.Api.Features.Bookings.ReleaseBooking;

public sealed partial class ReleaseBookingHandler
{
    private const string SelectHeadSql = """
        SELECT booking_id, customer_id, trip_id, status, failure_reason
        FROM bookings.bookings
        WHERE booking_id = @BookingId
        """;

    // Only an unpaid booking moves, and only once: the status in the WHERE clause is what makes an
    // abandon racing a payment result lose cleanly.
    private const string AbandonSql = """
        UPDATE bookings.bookings SET status = 'Failed', failure_reason = @Reason
        WHERE booking_id = @BookingId AND status IN ('SeatsHeld', 'PaymentPending')
        """;

    private const string SelectRefundSql = """
        SELECT reason, amount, status, requested_at, completed_at
        FROM cancellations.refunds
        WHERE booking_id = @BookingId
        """;

    private sealed class HeadRow
    {
        public Guid BookingId { get; init; }

        public string CustomerId { get; init; } = string.Empty;

        public Guid TripId { get; init; }

        public string Status { get; init; } = string.Empty;

        public string? FailureReason { get; init; }
    }

    private sealed class RefundRow
    {
        public string Reason { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string Status { get; init; } = string.Empty;

        public DateTime RequestedAt { get; init; }

        public DateTime? CompletedAt { get; init; }
    }

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
