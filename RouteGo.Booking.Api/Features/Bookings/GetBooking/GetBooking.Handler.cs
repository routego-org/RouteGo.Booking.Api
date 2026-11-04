namespace RouteGo.Booking.Api.Features.Bookings.GetBooking;

public sealed class GetBookingHandler
{
    private const string Currency = "INR";

    // The booking, its trip snapshot, and its cancellation and refund (when it has them) are one row; the
    // seats are the second result set, so the whole read is a single round trip.
    private const string SelectBookingSql = """
        SELECT b.booking_id, b.customer_id, b.status, b.contact_name, b.contact_email, b.total_amount,
               b.hold_expires_at, b.checkout_url, b.failure_reason, b.payment_failure_detail,
               b.seat_change_count, b.created_at, b.confirmed_at,
               t.trip_id, t.source_city_name, t.destination_city_name, t.service_date, t.departure_at,
               t.arrival_at, t.bus_name, t.bus_type, t.operator_name,
               c.kind AS cancellation_kind, c.reason AS cancellation_reason,
               c.refund_percent AS cancellation_refund_percent, c.refund_amount AS cancellation_refund_amount,
               c.cancelled_at,
               r.reason AS refund_reason, r.amount AS refund_amount, r.status AS refund_status,
               r.requested_at AS refund_requested_at, r.completed_at AS refund_completed_at
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        LEFT JOIN cancellations.cancellations c ON c.booking_id = b.booking_id
        LEFT JOIN cancellations.refunds r ON r.booking_id = b.booking_id
        WHERE b.booking_id = @BookingId;

        SELECT seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender
        FROM bookings.booking_seats
        WHERE booking_id = @BookingId
        ORDER BY seat_no;
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IClock _clock;

    public GetBookingHandler(IReaderConnectionFactory readerFactory, IClock clock)
    {
        _readerFactory = readerFactory;
        _clock = clock;
    }

    /// <summary>Reads one booking for a caller who is its customer or an admin.</summary>
    public async Task<GetBookingResult> HandleAsync(Guid bookingId, Caller caller, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        BookingData? data = await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, bookingId, cancellationToken), cancellationToken);
        if (data is null)
        {
            return new GetBookingResult(GetBookingOutcome.NotFound);
        }

        if (!MayRead(caller, data.Booking))
        {
            return new GetBookingResult(GetBookingOutcome.Forbidden);
        }

        return new GetBookingResult(GetBookingOutcome.Found, ToBooking(data, now));
    }

    private static async Task<BookingData?> ReadAsync(DbConnection connection, Guid bookingId, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(SelectBookingSql, new { BookingId = bookingId }, cancellationToken: cancellationToken);
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(command);
        BookingRow? booking = await results.ReadSingleOrDefaultAsync<BookingRow>();
        IEnumerable<SeatRow> seats = await results.ReadAsync<SeatRow>();
        if (booking is null)
        {
            return null;
        }

        return new BookingData(booking, seats.ToList());
    }

    // The booking's contract answers someone else's booking with 403, not 404, so existence is not hidden.
    private static bool MayRead(Caller caller, BookingRow booking)
    {
        if (caller.IsAdmin)
        {
            return true;
        }

        return booking.CustomerId == caller.AccountId;
    }

    private static Booking ToBooking(BookingData data, DateTimeOffset now)
    {
        BookingRow row = data.Booking;
        BookingStatus status = Enum.Parse<BookingStatus>(row.Status);
        DateTimeOffset? holdExpiresAt = UtcTimes.ToOffset(row.HoldExpiresAt);
        return new Booking
        {
            BookingId = row.BookingId,
            Status = status,
            Trip = ToTrip(row),
            ContactName = row.ContactName,
            ContactEmail = row.ContactEmail,
            Seats = ToSeats(data.Seats),
            TotalAmount = row.TotalAmount,
            Currency = Currency,
            HoldExpiresAt = ShowHoldExpiry(status, holdExpiresAt),
            CheckoutUrl = ShowCheckoutUrl(status, holdExpiresAt, row.CheckoutUrl, now),
            FailureReason = ToFailureReason(row.FailureReason),
            PaymentFailureDetail = row.PaymentFailureDetail,
            SeatChangeCount = row.SeatChangeCount,
            CreatedAt = UtcTimes.ToOffset(row.CreatedAt),
            ConfirmedAt = UtcTimes.ToOffset(row.ConfirmedAt),
            Cancellation = ToCancellation(row),
            Refund = ToRefund(row),
        };
    }

    private static DateTimeOffset? ShowHoldExpiry(BookingStatus status, DateTimeOffset? holdExpiresAt)
    {
        if (!BookingRules.IsUnpaid(status))
        {
            return null;
        }

        return holdExpiresAt;
    }

    // The payment page is only worth offering while the customer can still pay on it.
    private static string? ShowCheckoutUrl(BookingStatus status, DateTimeOffset? holdExpiresAt, string? checkoutUrl, DateTimeOffset now)
    {
        if (status != BookingStatus.PaymentPending || holdExpiresAt is null)
        {
            return null;
        }

        if (holdExpiresAt.Value <= now)
        {
            return null;
        }

        return checkoutUrl;
    }

    private static FailureReason? ToFailureReason(string? failureReason)
    {
        if (failureReason is null)
        {
            return null;
        }

        return Enum.Parse<FailureReason>(failureReason);
    }

    private static TripSummary ToTrip(BookingRow row)
    {
        return new TripSummary
        {
            TripId = row.TripId,
            SourceCityName = row.SourceCityName,
            DestinationCityName = row.DestinationCityName,
            ServiceDate = DateOnly.FromDateTime(row.ServiceDate),
            DepartureAt = UtcTimes.ToOffset(row.DepartureAt),
            ArrivalAt = UtcTimes.ToOffset(row.ArrivalAt),
            BusName = row.BusName,
            BusType = Enum.Parse<BusType>(row.BusType),
            OperatorName = row.OperatorName,
        };
    }

    private static List<BookingSeat> ToSeats(IReadOnlyList<SeatRow> rows)
    {
        var seats = new List<BookingSeat>();
        foreach (SeatRow row in rows)
        {
            seats.Add(new BookingSeat
            {
                SeatNo = row.SeatNo,
                SeatClass = row.SeatClass,
                Fare = row.Fare,
                PassengerName = row.PassengerName,
                PassengerAge = row.PassengerAge,
                PassengerGender = Enum.Parse<PassengerGender>(row.PassengerGender),
            });
        }

        return seats;
    }

    private static BookingCancellation? ToCancellation(BookingRow row)
    {
        if (row.CancellationKind is null)
        {
            return null;
        }

        return new BookingCancellation
        {
            Kind = Enum.Parse<CancellationKind>(row.CancellationKind),
            Reason = row.CancellationReason,
            RefundPercent = row.CancellationRefundPercent.GetValueOrDefault(),
            RefundAmount = row.CancellationRefundAmount.GetValueOrDefault(),
            CancelledAt = UtcTimes.ToOffset(row.CancelledAt.GetValueOrDefault()),
        };
    }

    private static BookingRefund? ToRefund(BookingRow row)
    {
        if (row.RefundReason is null)
        {
            return null;
        }

        return new BookingRefund
        {
            Reason = Enum.Parse<RefundReason>(row.RefundReason),
            Amount = row.RefundAmount.GetValueOrDefault(),
            Status = Enum.Parse<RefundStatus>(row.RefundStatus!),
            RequestedAt = UtcTimes.ToOffset(row.RefundRequestedAt.GetValueOrDefault()),
            CompletedAt = UtcTimes.ToOffset(row.RefundCompletedAt),
        };
    }

    private sealed record BookingData(BookingRow Booking, IReadOnlyList<SeatRow> Seats);

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }

        public string CustomerId { get; init; } = string.Empty;

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

        public string? CancellationKind { get; init; }

        public string? CancellationReason { get; init; }

        public int? CancellationRefundPercent { get; init; }

        public long? CancellationRefundAmount { get; init; }

        public DateTime? CancelledAt { get; init; }

        public string? RefundReason { get; init; }

        public long? RefundAmount { get; init; }

        public string? RefundStatus { get; init; }

        public DateTime? RefundRequestedAt { get; init; }

        public DateTime? RefundCompletedAt { get; init; }
    }

    private sealed class SeatRow
    {
        public string SeatNo { get; init; } = string.Empty;

        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }

        public string PassengerName { get; init; } = string.Empty;

        public int PassengerAge { get; init; }

        public string PassengerGender { get; init; } = string.Empty;
    }
}
