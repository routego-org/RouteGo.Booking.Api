namespace RouteGo.Booking.Api.Features.Bookings.ListBookings;

public sealed class ListBookingsHandler
{
    private const string Currency = "INR";

    // Listed: bookings that reached payment or beyond. Not listed: the customer's one unpaid booking, and a
    // booking that failed while being created (the customer was told at once and never saw a payment page).
    // The seat labels are aggregated in the same row and the refund is joined, so a page is one query plus
    // the count. The booking id breaks ties between bookings created at the same instant, so paging is stable.
    private const string SelectPageSql = """
        SELECT count(*)::int
        FROM bookings.bookings b
        WHERE b.customer_id = @CustomerId
          AND b.status IN ('Confirmed', 'Cancelled', 'Failed', 'Expired')
          AND (b.status <> 'Failed'
               OR b.failure_reason NOT IN ('SeatsUnavailable', 'InventoryUnavailable', 'PaymentUnavailable'));

        SELECT b.booking_id, b.status, b.total_amount, b.created_at,
               ARRAY(SELECT s.seat_no FROM bookings.booking_seats s WHERE s.booking_id = b.booking_id ORDER BY s.seat_no) AS seat_nos,
               t.trip_id, t.source_city_name, t.destination_city_name, t.service_date, t.departure_at,
               t.arrival_at, t.bus_name, t.bus_type, t.operator_name,
               r.reason AS refund_reason, r.amount AS refund_amount, r.status AS refund_status,
               r.requested_at AS refund_requested_at, r.completed_at AS refund_completed_at
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        LEFT JOIN cancellations.refunds r ON r.booking_id = b.booking_id
        WHERE b.customer_id = @CustomerId
          AND b.status IN ('Confirmed', 'Cancelled', 'Failed', 'Expired')
          AND (b.status <> 'Failed'
               OR b.failure_reason NOT IN ('SeatsUnavailable', 'InventoryUnavailable', 'PaymentUnavailable'))
        ORDER BY b.created_at DESC, b.booking_id
        OFFSET @Offset LIMIT @Limit;
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public ListBookingsHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Lists one page of the calling customer's own bookings, newest first.</summary>
    public async Task<BookingPage> HandleAsync(ListBookingsRequest request, Caller caller, CancellationToken cancellationToken)
    {
        int page = request.Page ?? ListBookingsRequest.DefaultPage;
        int pageSize = request.PageSize ?? ListBookingsRequest.DefaultPageSize;
        var parameters = new
        {
            CustomerId = caller.AccountId,
            Offset = (long)(page - 1) * pageSize,
            Limit = pageSize,
        };
        PageData data = await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, parameters, cancellationToken), cancellationToken);
        return ToPage(data, page, pageSize);
    }

    private static async Task<PageData> ReadAsync(DbConnection connection, object parameters, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(SelectPageSql, parameters, cancellationToken: cancellationToken);
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(command);
        int totalItems = await results.ReadSingleAsync<int>();
        IEnumerable<BookingRow> rows = await results.ReadAsync<BookingRow>();
        return new PageData(totalItems, rows.ToList());
    }

    private static BookingPage ToPage(PageData data, int page, int pageSize)
    {
        var items = new List<BookingSummary>();
        foreach (BookingRow row in data.Rows)
        {
            items.Add(ToSummary(row));
        }

        return new BookingPage { Items = items, Page = page, PageSize = pageSize, TotalItems = data.TotalItems };
    }

    private static BookingSummary ToSummary(BookingRow row)
    {
        return new BookingSummary
        {
            BookingId = row.BookingId,
            Status = Enum.Parse<BookingStatus>(row.Status),
            Trip = ToTrip(row),
            SeatNos = row.SeatNos,
            TotalAmount = row.TotalAmount,
            Currency = Currency,
            CreatedAt = UtcTimes.ToOffset(row.CreatedAt),
            Refund = ToRefund(row),
        };
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

    private sealed record PageData(int TotalItems, IReadOnlyList<BookingRow> Rows);

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }

        public string Status { get; init; } = string.Empty;

        public long TotalAmount { get; init; }

        public DateTime CreatedAt { get; init; }

        public string[] SeatNos { get; init; } = Array.Empty<string>();

        public Guid TripId { get; init; }

        public string SourceCityName { get; init; } = string.Empty;

        public string DestinationCityName { get; init; } = string.Empty;

        public DateTime ServiceDate { get; init; }

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }

        public string BusName { get; init; } = string.Empty;

        public string BusType { get; init; } = string.Empty;

        public string OperatorName { get; init; } = string.Empty;

        public string? RefundReason { get; init; }

        public long? RefundAmount { get; init; }

        public string? RefundStatus { get; init; }

        public DateTime? RefundRequestedAt { get; init; }

        public DateTime? RefundCompletedAt { get; init; }
    }
}
