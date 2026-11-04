namespace RouteGo.Booking.Api.Features.Cancellations.PreviewBookingCancellation;

/// <summary>
/// Works out the refund the booking's customer would get if they cancelled now, without changing
/// anything. The refund share comes from the settings' refund schedule and the time left to departure;
/// the amount is the booking's total times the share, rounded down to the whole rupee.
/// </summary>
public sealed class PreviewBookingCancellationHandler
{
    private const string SelectBookingSql = """
        SELECT b.customer_id, b.status, b.total_amount, t.departure_at
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        WHERE b.booking_id = @BookingId
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IBookingSettings _settings;
    private readonly IClock _clock;

    public PreviewBookingCancellationHandler(IReaderConnectionFactory readerFactory, IBookingSettings settings, IClock clock)
    {
        _readerFactory = readerFactory;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>The refund if the caller cancelled the booking now, or why they cannot.</summary>
    public async Task<PreviewBookingCancellationResult> HandleAsync(Guid bookingId, Caller caller, CancellationToken cancellationToken)
    {
        BookingSettingsValues settings = _settings.Current;
        DateTimeOffset now = _clock.UtcNow;
        BookingRow? booking = await GetBookingAsync(bookingId, cancellationToken);
        PreviewBookingCancellationResult? refusal = Refuse(booking, caller, now);
        if (refusal != null)
        {
            return refusal;
        }

        return Preview(bookingId, booking!, settings, now);
    }

    // Whose booking comes first, so a stranger learns nothing about its status or its trip.
    private static PreviewBookingCancellationResult? Refuse(BookingRow? booking, Caller caller, DateTimeOffset now)
    {
        if (booking is null)
        {
            return new PreviewBookingCancellationResult { Outcome = PreviewBookingCancellationOutcome.NotFound };
        }

        if (booking.CustomerId != caller.AccountId)
        {
            return new PreviewBookingCancellationResult { Outcome = PreviewBookingCancellationOutcome.NotTheCustomersBooking };
        }

        BookingStatus status = Enum.Parse<BookingStatus>(booking.Status);
        if (status != BookingStatus.Confirmed)
        {
            return new PreviewBookingCancellationResult
            {
                Outcome = PreviewBookingCancellationOutcome.BookingNotConfirmed,
                BookingStatus = status,
            };
        }

        DateTimeOffset departureAt = UtcTimes.ToOffset(booking.DepartureAt);
        if (BookingRules.HasDeparted(departureAt, now))
        {
            return new PreviewBookingCancellationResult
            {
                Outcome = PreviewBookingCancellationOutcome.TripDeparted,
                DepartedAt = departureAt,
            };
        }

        return null;
    }

    private static PreviewBookingCancellationResult Preview(
        Guid bookingId, BookingRow booking, BookingSettingsValues settings, DateTimeOffset now)
    {
        DateTimeOffset departureAt = UtcTimes.ToOffset(booking.DepartureAt);
        int refundPercent = RefundRules.CalculatePercent(departureAt - now, settings.CancellationTiers);
        long refundAmount = RefundRules.CalculateAmount(booking.TotalAmount, refundPercent);
        var preview = new CancellationPreview
        {
            BookingId = bookingId,
            TotalAmount = booking.TotalAmount,
            Currency = BookingEventTypes.Currency,
            RefundPercent = refundPercent,
            RefundAmount = refundAmount,
            DepartureAt = departureAt,
            CalculatedAt = now,
        };
        return new PreviewBookingCancellationResult { Outcome = PreviewBookingCancellationOutcome.Previewed, Response = preview };
    }

    private async Task<BookingRow?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using DbConnection connection = _readerFactory.CreateReadConnection();
        return await connection.QuerySingleOrDefaultAsync<BookingRow>(new CommandDefinition(
            SelectBookingSql, new { BookingId = bookingId }, cancellationToken: cancellationToken));
    }

    private sealed class BookingRow
    {
        public string CustomerId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public long TotalAmount { get; init; }

        public DateTime DepartureAt { get; init; }
    }
}
