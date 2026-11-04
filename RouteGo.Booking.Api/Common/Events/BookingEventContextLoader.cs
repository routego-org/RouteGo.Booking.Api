namespace RouteGo.Booking.Api.Common.Events;

/// <summary>Loads a <see cref="BookingEventContext"/> over the writer's own transaction, so it sees the change just made.</summary>
public static class BookingEventContextLoader
{
    private const string SelectBookingSql = """
        SELECT b.booking_id, b.customer_id, b.contact_name, b.contact_email, b.trip_id, b.status, b.total_amount,
               b.payment_id, b.confirmed_at, b.seat_change_count,
               t.operator_id, t.operator_name, t.operator_email, t.source_city_name, t.destination_city_name,
               t.service_date, t.departure_at, t.arrival_at, t.bus_name, t.bus_type, t.is_ac
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        WHERE b.booking_id = @BookingId
        """;

    private const string SelectSeatsSql = """
        SELECT seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender
        FROM bookings.booking_seats
        WHERE booking_id = @BookingId
        ORDER BY seat_no
        """;

    /// <summary>The booking as an event describes it. The booking must exist and its trip snapshot must carry the trip's details.</summary>
    /// <param name="writer">The writer connection whose transaction made the change.</param>
    /// <param name="bookingId">The booking.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    public static async Task<BookingEventContext> LoadAsync(IWriterConnection writer, Guid bookingId, CancellationToken cancellationToken)
    {
        var parameters = new { BookingId = bookingId };
        BookingRow row = await writer.Connection.QuerySingleAsync<BookingRow>(new CommandDefinition(
            SelectBookingSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<BookingEventSeat> seats = await writer.Connection.QueryAsync<BookingEventSeat>(new CommandDefinition(
            SelectSeatsSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        return ToContext(row, seats.ToList());
    }

    private static BookingEventContext ToContext(BookingRow row, IReadOnlyList<BookingEventSeat> seats)
    {
        return new BookingEventContext
        {
            BookingId = row.BookingId,
            CustomerId = row.CustomerId,
            ContactName = row.ContactName,
            ContactEmail = row.ContactEmail,
            TripId = row.TripId,
            Status = row.Status,
            TotalAmount = row.TotalAmount,
            PaymentId = row.PaymentId,
            ConfirmedAt = UtcTimes.ToOffset(row.ConfirmedAt),
            SeatChangeCount = row.SeatChangeCount,
            OperatorId = row.OperatorId,
            OperatorName = row.OperatorName,
            OperatorEmail = row.OperatorEmail,
            SourceCityName = row.SourceCityName,
            DestinationCityName = row.DestinationCityName,
            ServiceDate = DateOnly.FromDateTime(row.ServiceDate).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DepartureAt = UtcTimes.ToOffset(row.DepartureAt),
            ArrivalAt = UtcTimes.ToOffset(row.ArrivalAt),
            BusName = row.BusName,
            BusType = row.BusType,
            IsAc = row.IsAc,
            Seats = seats,
        };
    }

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }

        public string CustomerId { get; init; } = string.Empty;

        public string ContactName { get; init; } = string.Empty;

        public string ContactEmail { get; init; } = string.Empty;

        public Guid TripId { get; init; }

        public string Status { get; init; } = string.Empty;

        public long TotalAmount { get; init; }

        public Guid? PaymentId { get; init; }

        public DateTime? ConfirmedAt { get; init; }

        public int SeatChangeCount { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public string OperatorName { get; init; } = string.Empty;

        public string OperatorEmail { get; init; } = string.Empty;

        public string SourceCityName { get; init; } = string.Empty;

        public string DestinationCityName { get; init; } = string.Empty;

        public DateTime ServiceDate { get; init; }

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }

        public string BusName { get; init; } = string.Empty;

        public string BusType { get; init; } = string.Empty;

        public bool IsAc { get; init; }
    }
}
