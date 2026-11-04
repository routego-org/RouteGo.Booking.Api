using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RouteGo.Booking.Api.Features.Bookings.PublishBookingEvents;

[Collection(DatabaseCollection.Name)]
public sealed class PublishBookingEventsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    // The publisher's advisory lock key, held here by a second session to stand in for another running copy.
    private const long PublisherLockKey = 4_711_201;

    private const string InsertEventSql = """
        INSERT INTO bookings.booking_events (event_id, event_type, stream, booking_id, event_data, correlation_id, recorded_at)
        VALUES (@EventId, @EventType, @Stream, @BookingId, @EventData, @CorrelationId, @RecordedAt)
        """;

    private const string SentAtSql = "SELECT sent_at FROM bookings.booking_events WHERE event_id = @EventId";

    private readonly DatabaseFixture _database;
    private readonly RecordingPublisher _publisher = new RecordingPublisher();
    private readonly CapturingLogger _logger = new CapturingLogger();
    private readonly FixedClock _clock = new FixedClock(_now);
    private Guid _bookingId;

    public PublishBookingEventsTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.ResetBusinessTablesAsync();
        var trip = new TripSeed();
        await BookingSeed.SeedTripAsync(_database, trip);
        var booking = new BookingSeedSpec { TripId = trip.TripId };
        await BookingSeed.SeedBookingAsync(_database, booking);
        _bookingId = booking.BookingId;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_RecordedBookingEvent_SendsItWithTheEnvelopeAndMarksItSent()
    {
        // Arrange
        Guid eventId = await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-3));

        // Act
        PublishBookingEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, result.SentCount);
        SentEvent sent = Assert.Single(_publisher.Sent);
        Assert.False(sent.IsAudit);
        Assert.Equal("BookingConfirmed", sent.EventType);
        Assert.Equal(eventId, sent.Envelope.GetProperty("eventId").GetGuid());
        Assert.Equal("BookingConfirmed", sent.Envelope.GetProperty("eventType").GetString());
        Assert.Equal(1, sent.Envelope.GetProperty("version").GetInt32());
        Assert.Equal("booking-service", sent.Envelope.GetProperty("producer").GetString());
        Assert.Equal("corr-BookingConfirmed", sent.Envelope.GetProperty("correlationId").GetString());
        Assert.Equal(_now.AddSeconds(-3), sent.Envelope.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(_bookingId, sent.Envelope.GetProperty("data").GetProperty("bookingId").GetGuid());
        Assert.NotNull(await _database.QueryScalarAsync<DateTime?>(SentAtSql, new { EventId = eventId }));
    }

    [Fact]
    public async Task HandleAsync_CancellationEvents_GoToTheBookingStreamAndAuditRecordedToTheAuditStream()
    {
        // Arrange
        await SeedEventAsync("BookingCancelled", _now.AddSeconds(-5));
        await SeedEventAsync("RefundRequested", _now.AddSeconds(-4));
        await SeedEventAsync("AuditRecorded", _now.AddSeconds(-3));

        // Act
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(3, _publisher.Sent.Count);
        Assert.False(_publisher.Sent[0].IsAudit);
        Assert.False(_publisher.Sent[1].IsAudit);
        Assert.True(_publisher.Sent[2].IsAudit);
    }

    [Fact]
    public async Task HandleAsync_EventRecordedBeforeAStop_IsSentByTheRunAfterTheRestart()
    {
        // Arrange: the change and its event were saved, and Booking stopped before any run happened.
        Guid eventId = await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-30));

        // Act: a brand-new handler, as after a restart.
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        SentEvent sent = Assert.Single(_publisher.Sent);
        Assert.Equal(eventId, sent.Envelope.GetProperty("eventId").GetGuid());
    }

    [Fact]
    public async Task HandleAsync_TwoEventsRecordedInOrder_SendsThemInThatOrder()
    {
        // Arrange
        await SeedEventAsync("BookingCancelled", _now.AddSeconds(-2));
        await SeedEventAsync("RefundRequested", _now.AddSeconds(-1));

        // Act
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal("BookingCancelled", _publisher.Sent[0].EventType);
        Assert.Equal("RefundRequested", _publisher.Sent[1].EventType);
    }

    [Fact]
    public async Task HandleAsync_EventSentButNeverAcknowledged_IsSentAgainWithTheSameEventId()
    {
        // Arrange
        await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-3));
        _publisher.FailNextAfterAccepting = true;
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Act
        await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, _publisher.Sent.Count);
        Guid firstId = _publisher.Sent[0].Envelope.GetProperty("eventId").GetGuid();
        Guid secondId = _publisher.Sent[1].Envelope.GetProperty("eventId").GetGuid();
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task HandleAsync_MessagingDownThenBack_HoldsEveryEventBackThenSendsAllInOrder()
    {
        // Arrange
        await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-3));
        await SeedEventAsync("BookingCancelled", _now.AddSeconds(-2));
        await SeedEventAsync("AuditRecorded", _now.AddSeconds(-1));
        _publisher.IsDown = true;

        // Act
        PublishBookingEventsResult whileDown = await CreateHandler().HandleAsync(CancellationToken.None);
        _publisher.IsDown = false;
        PublishBookingEventsResult afterRecovery = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, whileDown.SentCount);
        Assert.Equal(3, afterRecovery.SentCount);
        Assert.Equal("BookingConfirmed", _publisher.Sent[0].EventType);
        Assert.Equal("BookingCancelled", _publisher.Sent[1].EventType);
        Assert.Equal("AuditRecorded", _publisher.Sent[2].EventType);
    }

    [Fact]
    public async Task HandleAsync_OldestUnsentEventOverFiveMinutesOld_RaisesTheDelayAlert()
    {
        // Arrange
        await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-301));
        _publisher.IsDown = true;

        // Act
        PublishBookingEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsDelayed);
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Message.Contains("BOOKING_EVENT_DELAYED"));
        Assert.Equal(LogLevel.Error, alert.Level);
        Assert.Contains("301 seconds", alert.Message);
    }

    [Fact]
    public async Task HandleAsync_OldestUnsentEventUnderFiveMinutesOld_RaisesNoAlert()
    {
        // Arrange
        await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-299));
        _publisher.IsDown = true;

        // Act
        PublishBookingEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.False(result.IsDelayed);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Message.Contains("BOOKING_EVENT_DELAYED"));
    }

    [Fact]
    public async Task HandleAsync_AnotherPublisherHoldsTheLock_DoesNothing()
    {
        // Arrange
        await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-3));
        await using var other = new NpgsqlConnection(_database.ConnectionString);
        await other.OpenAsync();
        await using NpgsqlTransaction transaction = await other.BeginTransactionAsync();
        await using var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({PublisherLockKey})", other, transaction);
        await lockCommand.ExecuteNonQueryAsync();

        // Act
        PublishBookingEventsResult result = await CreateHandler().HandleAsync(CancellationToken.None);

        // Assert
        Assert.True(result.IsSkipped);
        Assert.Equal(0, result.SentCount);
        Assert.Empty(_publisher.Sent);
    }

    [Fact]
    public async Task ExecuteAsync_RunningService_SendsARecordedEventWithinTheNextRuns()
    {
        // Arrange
        await SeedEventAsync("BookingConfirmed", _now.AddSeconds(-3));
        var service = new PublishBookingEventsService(TestWriters.CreateWriter(_database), _publisher, _clock, _logger);

        // Act
        await service.StartAsync(CancellationToken.None);
        await _publisher.WaitForSentAsync(1, TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal("BookingConfirmed", Assert.Single(_publisher.Sent).EventType);
    }

    private PublishBookingEventsHandler CreateHandler()
    {
        return new PublishBookingEventsHandler(TestWriters.CreateWriter(_database), _publisher, _clock, _logger);
    }

    private async Task<Guid> SeedEventAsync(string eventType, DateTimeOffset recordedAt)
    {
        var eventId = Guid.NewGuid();
        string stream = "BookingEvents";
        if (eventType == "AuditRecorded")
        {
            stream = "AuditEvents";
        }

        var parameters = new
        {
            EventId = eventId,
            EventType = eventType,
            Stream = stream,
            BookingId = _bookingId,
            EventData = $"{{\"bookingId\":\"{_bookingId}\"}}",
            CorrelationId = $"corr-{eventType}",
            RecordedAt = recordedAt.UtcDateTime,
        };
        await _database.SeedAsync(InsertEventSql, parameters);
        return eventId;
    }

    private sealed record SentEvent(bool IsAudit, string EventType, JsonElement Envelope);

    private sealed class RecordingPublisher : IBookingEventPublisher
    {
        private readonly object _gate = new object();
        private readonly List<SentEvent> _sent = new List<SentEvent>();

        public bool IsDown { get; set; }

        public bool FailNextAfterAccepting { get; set; }

        public IReadOnlyList<SentEvent> Sent
        {
            get
            {
                lock (_gate)
                {
                    return _sent.ToList();
                }
            }
        }

        public Task PublishAsync(bool isAuditEvent, string eventType, string envelopeJson, CancellationToken cancellationToken)
        {
            if (IsDown)
            {
                throw new InvalidOperationException("The messaging service is down.");
            }

            using var document = JsonDocument.Parse(envelopeJson);
            lock (_gate)
            {
                _sent.Add(new SentEvent(isAuditEvent, eventType, document.RootElement.Clone()));
            }

            if (FailNextAfterAccepting)
            {
                FailNextAfterAccepting = false;
                throw new InvalidOperationException("The acknowledgement was lost.");
            }

            return Task.CompletedTask;
        }

        public async Task WaitForSentAsync(int count, TimeSpan timeout)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (Sent.Count < count && stopwatch.Elapsed < timeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
