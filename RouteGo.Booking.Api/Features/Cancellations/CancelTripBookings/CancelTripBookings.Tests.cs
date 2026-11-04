using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RouteGo.Booking.Api.Features.Cancellations.CancelTripBookings;

[Collection(DatabaseCollection.Name)]
public sealed class CancelTripBookingsTests
{
    private const string Reason = "Bus breakdown";
    private const string BookingSql = "SELECT status, failure_reason FROM bookings.bookings WHERE booking_id = @BookingId";
    private const string CancellationSql = """
        SELECT kind, reason, refund_percent, refund_amount, cancelled_by FROM cancellations.cancellations WHERE booking_id = @BookingId
        """;

    private const string RefundSql = """
        SELECT payment_id, reason, amount, refund_key, status FROM cancellations.refunds WHERE booking_id = @BookingId
        """;

    private const string EventsSql = """
        SELECT event_type, booking_id, event_data FROM bookings.booking_events ORDER BY recorded_at, event_id
        """;

    private const string SnapshotSql = """
        SELECT is_cancelled, cancellation_reason, cancelled_at, operator_id FROM trip_snapshots.trip_snapshots WHERE trip_id = @TripId
        """;

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;

    public CancelTripBookingsTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_ConfirmedBookings_AreCancelledWithAFullRefundAndAnnounced()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        BookingSeedSpec first = await SeedConfirmedAsync(tripId, "C-1", "3A", "3B");
        BookingSeedSpec second = await SeedConfirmedAsync(tripId, "C-2", "4C");

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Cancelled(tripId, Reason), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        await AssertCancelledAsync(first, 1600);
        await AssertCancelledAsync(second, 600);
        IReadOnlyList<EventRow> events = await TestQueries.QueryAsync<EventRow>(_database, EventsSql);
        Assert.Equal(4, events.Count);
        Assert.DoesNotContain(events, row => row.EventType == "AuditRecorded");
        Assert.Equal(2, events.Count(row => row.EventType == "BookingCancelled"));
        Assert.Equal(2, events.Count(row => row.EventType == "RefundRequested"));
        EventRow cancelled = events.First(row => row.EventType == "BookingCancelled" && row.BookingId == first.BookingId);
        using var document = JsonDocument.Parse(cancelled.EventData);
        Assert.Equal("OperatorCancelled", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(Reason, document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(100, document.RootElement.GetProperty("refundPercent").GetInt32());
        Assert.Equal(1600, document.RootElement.GetProperty("refundAmount").GetInt64());
        EventRow refund = events.First(row => row.EventType == "RefundRequested" && row.BookingId == first.BookingId);
        using var refundDocument = JsonDocument.Parse(refund.EventData);
        Assert.Equal(first.PaymentId, refundDocument.RootElement.GetProperty("paymentId").GetGuid());
        Assert.Equal(first.BookingId + ":cancel", refundDocument.RootElement.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task HandleAsync_UnpaidBookings_AreFailedAsTripCancelledAndAnnouncedWithContactAndRoute()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        var pending = new BookingSeedSpec { TripId = tripId, CustomerId = "C-1", Status = BookingStatus.PaymentPending };
        var held = new BookingSeedSpec
        {
            TripId = tripId,
            CustomerId = "C-2",
            Status = BookingStatus.SeatsHeld,
            Passengers = new List<SeedPassenger> { new SeedPassenger("4C", "Asha Rao", 600) },
        };
        await BookingSeed.SeedBookingAsync(_database, pending);
        await BookingSeed.SeedBookingAsync(_database, held);

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Cancelled(tripId, Reason), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        BookingRow pendingRow = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { pending.BookingId });
        BookingRow heldRow = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { held.BookingId });
        Assert.Equal("Failed", pendingRow.Status);
        Assert.Equal("TripCancelled", pendingRow.FailureReason);
        Assert.Equal("Failed", heldRow.Status);
        IReadOnlyList<EventRow> events = await TestQueries.QueryAsync<EventRow>(_database, EventsSql);
        Assert.Equal(2, events.Count);
        Assert.All(events, row => Assert.Equal("BookingFailed", row.EventType));
        using var document = JsonDocument.Parse(events.First(row => row.BookingId == pending.BookingId).EventData);
        Assert.Equal("TripCancelled", document.RootElement.GetProperty("reason").GetString());
        Assert.Equal("priya@example.com", document.RootElement.GetProperty("contact").GetProperty("email").GetString());
        Assert.Equal("Mumbai", document.RootElement.GetProperty("trip").GetProperty("source").GetString());
        Assert.Equal("Pune", document.RootElement.GetProperty("trip").GetProperty("destination").GetString());
        Assert.True(document.RootElement.GetProperty("trip").TryGetProperty("departureUtc", out JsonElement _));
    }

    [Fact]
    public async Task HandleAsync_FinishedAndInitiatedBookings_AreLeftAloneWithNoEvent()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        var failed = new BookingSeedSpec { TripId = tripId, CustomerId = "C-1", Status = BookingStatus.Failed, FailureReason = FailureReason.Abandoned };
        var expired = new BookingSeedSpec { TripId = tripId, CustomerId = "C-2", Status = BookingStatus.Expired };
        var cancelled = new BookingSeedSpec
        {
            TripId = tripId,
            CustomerId = "C-3",
            Status = BookingStatus.Cancelled,
            ConfirmedAt = _now.AddDays(-2),
            PaymentId = Guid.NewGuid(),
        };
        var initiated = new BookingSeedSpec { TripId = tripId, CustomerId = "C-4", Status = BookingStatus.Initiated };
        await BookingSeed.SeedBookingAsync(_database, failed);
        await BookingSeed.SeedBookingAsync(_database, expired);
        await BookingSeed.SeedBookingAsync(_database, cancelled);
        await BookingSeed.SeedBookingAsync(_database, initiated);

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Cancelled(tripId, Reason), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        Assert.Equal("Failed", (await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { failed.BookingId })).Status);
        Assert.Equal("Expired", (await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { expired.BookingId })).Status);
        Assert.Equal("Cancelled", (await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { cancelled.BookingId })).Status);
        Assert.Equal("Initiated", (await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { initiated.BookingId })).Status);
        Assert.Empty(await TestQueries.QueryAsync<EventRow>(_database, EventsSql));
    }

    [Fact]
    public async Task HandleAsync_KnownTrip_IsMarkedCancelledWithFleetsReasonAndKeepsItsDetails()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });

        // Act
        await CreateHandler().HandleAsync(Cancelled(tripId, "Schedule changed"), CancellationToken.None);

        // Assert
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.True(snapshot.IsCancelled);
        Assert.Equal("Schedule changed", snapshot.CancellationReason);
        Assert.Equal(_now.UtcDateTime, snapshot.CancelledAt.ToUniversalTime());
        Assert.Equal("OP-1", snapshot.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_TripNotYetKnown_IsRememberedAsACancelledSnapshotHoldingOnlyTheTripId()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Cancelled(tripId, Reason), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.True(snapshot.IsCancelled);
        Assert.Null(snapshot.OperatorId);
    }

    [Fact]
    public async Task HandleAsync_SameCancellationTwiceOrAnotherOne_ChangesNothingAndAnnouncesNothingAgain()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        BookingSeedSpec booking = await SeedConfirmedAsync(tripId, "C-1", "3A");
        CancelTripBookingsHandler handler = CreateHandler();
        await handler.HandleAsync(Cancelled(tripId, Reason), CancellationToken.None);

        // Act
        EventOutcome repeat = await handler.HandleAsync(Cancelled(tripId, Reason), CancellationToken.None);
        EventOutcome another = await handler.HandleAsync(Cancelled(tripId, "Other reason"), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Applied, repeat.Kind);
        Assert.Equal(EventOutcomeKind.Applied, another.Kind);
        Assert.Equal(2, (await TestQueries.QueryAsync<EventRow>(_database, EventsSql)).Count);
        SnapshotRow snapshot = await TestQueries.QuerySingleAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId });
        Assert.Equal(Reason, snapshot.CancellationReason);
        CancellationRow cancellation = await TestQueries.QuerySingleAsync<CancellationRow>(_database, CancellationSql, new { booking.BookingId });
        Assert.Equal(Reason, cancellation.Reason);
    }

    [Theory]
    [InlineData("tripId")]
    [InlineData("reason")]
    public async Task HandleAsync_DataMissingAMember_IsRejectedAsInvalidAndChangesNothing(string missing)
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        var data = new Dictionary<string, object?> { ["tripId"] = tripId, ["reason"] = Reason };
        data.Remove(missing);

        // Act
        EventOutcome outcome = await CreateHandler().HandleAsync(Event(data), CancellationToken.None);

        // Assert
        Assert.Equal(EventOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(EventOutcome.InvalidEventData, outcome.Reason);
        Assert.Contains("E-9", outcome.Problem);
        Assert.Empty(await TestQueries.QueryAsync<SnapshotRow>(_database, SnapshotSql, new { TripId = tripId }));
    }

    [Fact]
    public async Task HandleAsync_FailureAfterSomeBookings_RetryEndsTheRestAndNeverRepeatsOne()
    {
        // Arrange
        await _database.ResetBusinessTablesAsync();
        var tripId = Guid.NewGuid();
        await BookingSeed.SeedTripAsync(_database, new TripSeed { TripId = tripId });
        BookingSeedSpec first = await SeedConfirmedAsync(tripId, "C-1", "3A");
        BookingSeedSpec second = await SeedConfirmedAsync(tripId, "C-2", "3B");
        BookingSeedSpec third = await SeedConfirmedAsync(tripId, "C-3", "4C");
        var failing = new FailingWriterFactory(TestWriters.CreateWriter(_database), 3);
        ReceivedEvent receivedEvent = Cancelled(tripId, Reason);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateHandler(failing).HandleAsync(receivedEvent, CancellationToken.None));
        int endedBeforeRetry = (await TestQueries.QueryAsync<EventRow>(_database, EventsSql)).Count;
        EventOutcome outcome = await CreateHandler().HandleAsync(receivedEvent, CancellationToken.None);

        // Assert
        Assert.Equal(2, endedBeforeRetry);
        Assert.Equal(EventOutcomeKind.Applied, outcome.Kind);
        await AssertCancelledAsync(first, 800);
        await AssertCancelledAsync(second, 800);
        await AssertCancelledAsync(third, 600);
        IReadOnlyList<EventRow> events = await TestQueries.QueryAsync<EventRow>(_database, EventsSql);
        Assert.Equal(6, events.Count);
        Assert.Equal(3, events.Count(row => row.EventType == "RefundRequested"));
    }

    private static CancelTripBookingsHandler CreateHandler(IWriterConnectionFactory writerFactory, DateTimeOffset now)
    {
        return new CancelTripBookingsHandler(writerFactory, new FixedClock(now), NullLogger<CancelTripBookingsHandler>.Instance);
    }

    private static CancelTripBookingsHandler CreateHandler(IWriterConnectionFactory writerFactory)
    {
        return CreateHandler(writerFactory, _now);
    }

    private CancelTripBookingsHandler CreateHandler()
    {
        return CreateHandler(TestWriters.CreateWriter(_database));
    }

    private async Task<BookingSeedSpec> SeedConfirmedAsync(Guid tripId, string customerId, params string[] seatNos)
    {
        var passengers = new List<SeedPassenger>();
        foreach (string seatNo in seatNos)
        {
            long fare = 800;
            if (seatNo == "4C")
            {
                fare = 600;
            }

            passengers.Add(new SeedPassenger(seatNo, "Passenger " + seatNo, fare));
        }

        var booking = new BookingSeedSpec
        {
            TripId = tripId,
            CustomerId = customerId,
            Status = BookingStatus.Confirmed,
            ConfirmedAt = _now.AddDays(-3),
            PaymentId = Guid.NewGuid(),
            Passengers = passengers,
        };
        await BookingSeed.SeedBookingAsync(_database, booking);
        return booking;
    }

    private async Task AssertCancelledAsync(BookingSeedSpec booking, long total)
    {
        BookingRow row = await TestQueries.QuerySingleAsync<BookingRow>(_database, BookingSql, new { booking.BookingId });
        Assert.Equal("Cancelled", row.Status);
        CancellationRow cancellation = await TestQueries.QuerySingleAsync<CancellationRow>(_database, CancellationSql, new { booking.BookingId });
        Assert.Equal("OperatorCancelled", cancellation.Kind);
        Assert.Equal(Reason, cancellation.Reason);
        Assert.Equal(100, cancellation.RefundPercent);
        Assert.Equal(total, cancellation.RefundAmount);
        Assert.Null(cancellation.CancelledBy);
        RefundRow refund = await TestQueries.QuerySingleAsync<RefundRow>(_database, RefundSql, new { booking.BookingId });
        Assert.Equal("Cancellation", refund.Reason);
        Assert.Equal(total, refund.Amount);
        Assert.Equal(booking.BookingId + ":cancel", refund.RefundKey);
        Assert.Equal("Requested", refund.Status);
        Assert.Equal(booking.PaymentId, refund.PaymentId);
    }

    private static ReceivedEvent Cancelled(Guid tripId, string reason)
    {
        return Event(new Dictionary<string, object?> { ["tripId"] = tripId, ["reason"] = reason });
    }

    private static ReceivedEvent Event(Dictionary<string, object?> data)
    {
        return new ReceivedEvent
        {
            EventId = "E-9",
            EventType = "TripCancelled",
            OccurredAt = _now,
            CorrelationId = "corr-cancel",
            Data = JsonSerializer.SerializeToElement(data),
        };
    }

    // Opens normally until the given open, then fails every later one, standing for a database that goes away.
    private sealed class FailingWriterFactory : IWriterConnectionFactory
    {
        private readonly IWriterConnectionFactory _inner;
        private readonly int _failFromOpen;
        private int _opens;

        public FailingWriterFactory(IWriterConnectionFactory inner, int failFromOpen)
        {
            _inner = inner;
            _failFromOpen = failFromOpen;
        }

        public async Task<IWriterConnection> OpenWriteTransactionAsync(string action, CancellationToken cancellationToken = default)
        {
            return await OpenWriteTransactionAsync(action, null, cancellationToken);
        }

        public async Task<IWriterConnection> OpenWriteTransactionAsync(string action, string? reason, CancellationToken cancellationToken = default)
        {
            _opens++;
            if (_opens >= _failFromOpen)
            {
                throw new InvalidOperationException("The database went away.");
            }

            return await _inner.OpenWriteTransactionAsync(action, reason, cancellationToken);
        }
    }

    private sealed class BookingRow
    {
        public string Status { get; init; } = string.Empty;

        public string? FailureReason { get; init; }
    }

    private sealed class CancellationRow
    {
        public string Kind { get; init; } = string.Empty;

        public string? Reason { get; init; }

        public int RefundPercent { get; init; }

        public long RefundAmount { get; init; }

        public string? CancelledBy { get; init; }
    }

    private sealed class RefundRow
    {
        public Guid PaymentId { get; init; }

        public string Reason { get; init; } = string.Empty;

        public long Amount { get; init; }

        public string RefundKey { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }

    private sealed class EventRow
    {
        public string EventType { get; init; } = string.Empty;

        public Guid BookingId { get; init; }

        public string EventData { get; init; } = string.Empty;
    }

    private sealed class SnapshotRow
    {
        public bool IsCancelled { get; init; }

        public string? CancellationReason { get; init; }

        public DateTime CancelledAt { get; init; }

        public string? OperatorId { get; init; }
    }
}
