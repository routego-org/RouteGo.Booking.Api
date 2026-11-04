namespace RouteGo.Booking.Api.Features.Bookings.SearchBookings;

public sealed class SearchBookingsHandler
{
    private const string Currency = "INR";

    // A criterion that is not given matches every booking; the ones given must all match. The email is compared
    // in lower case on both sides, which is the expression the lower-case email index holds. The booking id
    // breaks ties between bookings created at the same instant, so paging is stable.
    private const string SelectPageSql = """
        SELECT count(*)::int
        FROM bookings.bookings b
        WHERE (CAST(@BookingId AS uuid) IS NULL OR b.booking_id = @BookingId)
          AND (CAST(@TripId AS uuid) IS NULL OR b.trip_id = @TripId)
          AND (CAST(@Email AS text) IS NULL OR lower(b.contact_email) = lower(@Email));

        SELECT b.booking_id, b.status, b.contact_name, b.contact_email, b.total_amount, b.created_at,
               ARRAY(SELECT s.seat_no FROM bookings.booking_seats s WHERE s.booking_id = b.booking_id ORDER BY s.seat_no) AS seat_nos,
               t.trip_id, t.source_city_name, t.destination_city_name, t.service_date, t.departure_at,
               t.arrival_at, t.bus_name, t.bus_type, t.operator_name
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        WHERE (CAST(@BookingId AS uuid) IS NULL OR b.booking_id = @BookingId)
          AND (CAST(@TripId AS uuid) IS NULL OR b.trip_id = @TripId)
          AND (CAST(@Email AS text) IS NULL OR lower(b.contact_email) = lower(@Email))
        ORDER BY b.created_at DESC, b.booking_id
        OFFSET @Offset LIMIT @Limit;
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public SearchBookingsHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Finds one page of the bookings that match every criterion given, in every status.</summary>
    public async Task<AdminBookingPage> HandleAsync(SearchBookingsRequest request, CancellationToken cancellationToken)
    {
        int page = request.Page ?? SearchBookingsRequest.DefaultPage;
        int pageSize = request.PageSize ?? SearchBookingsRequest.DefaultPageSize;
        var parameters = new
        {
            request.Search.BookingId,
            request.Search.TripId,
            Email = ToEmail(request.Search.ContactEmail),
            Offset = (long)(page - 1) * pageSize,
            Limit = pageSize,
        };
        PageData data = await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, parameters, cancellationToken), cancellationToken);
        return ToPage(data, page, pageSize);
    }

    // A blank email is no criterion at all, so it must not turn into "match the empty email".
    private static string? ToEmail(string? contactEmail)
    {
        if (string.IsNullOrWhiteSpace(contactEmail))
        {
            return null;
        }

        return contactEmail.Trim();
    }

    private static async Task<PageData> ReadAsync(DbConnection connection, object parameters, CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(SelectPageSql, parameters, cancellationToken: cancellationToken);
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(command);
        int totalItems = await results.ReadSingleAsync<int>();
        IEnumerable<BookingRow> rows = await results.ReadAsync<BookingRow>();
        return new PageData(totalItems, rows.ToList());
    }

    private static AdminBookingPage ToPage(PageData data, int page, int pageSize)
    {
        var items = new List<AdminBookingSummary>();
        foreach (BookingRow row in data.Rows)
        {
            items.Add(ToSummary(row));
        }

        return new AdminBookingPage { Items = items, Page = page, PageSize = pageSize, TotalItems = data.TotalItems };
    }

    private static AdminBookingSummary ToSummary(BookingRow row)
    {
        return new AdminBookingSummary
        {
            BookingId = row.BookingId,
            Status = Enum.Parse<BookingStatus>(row.Status),
            ContactName = row.ContactName,
            ContactEmail = row.ContactEmail,
            Trip = ToTrip(row),
            SeatNos = row.SeatNos,
            TotalAmount = row.TotalAmount,
            Currency = Currency,
            CreatedAt = UtcTimes.ToOffset(row.CreatedAt),
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

    private sealed record PageData(int TotalItems, IReadOnlyList<BookingRow> Rows);

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }

        public string Status { get; init; } = string.Empty;

        public string ContactName { get; init; } = string.Empty;

        public string ContactEmail { get; init; } = string.Empty;

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
    }
}
