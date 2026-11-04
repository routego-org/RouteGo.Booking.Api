namespace RouteGo.Booking.Api.Features.Bookings.PublishBookingEvents;

/// <summary>What one publishing run did.</summary>
/// <param name="SentCount">Events sent and marked as sent by this run.</param>
/// <param name="IsDelayed">True when the oldest unsent event is more than five minutes old, so the alert was raised.</param>
/// <param name="IsSkipped">True when another publisher held the lock, so this run did nothing.</param>
public sealed record PublishBookingEventsResult(int SentCount, bool IsDelayed, bool IsSkipped);

/// <summary>
/// Runs the publisher every five seconds for as long as the API process runs. A failed run is logged
/// and the next one retries, so a messaging or database outage never stops the loop.
/// </summary>
public sealed class PublishBookingEventsService : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

    private readonly PublishBookingEventsHandler _handler;
    private readonly ILogger<PublishBookingEventsService> _logger;

    public PublishBookingEventsService(
        IWriterConnectionFactory writerFactory,
        IBookingEventPublisher publisher,
        IClock clock,
        ILoggerFactory loggerFactory)
    {
        _handler = new PublishBookingEventsHandler(
            writerFactory, publisher, clock, loggerFactory.CreateLogger<PublishBookingEventsHandler>());
        _logger = loggerFactory.CreateLogger<PublishBookingEventsService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _handler.HandleAsync(stoppingToken);
        }
        // A timeout inside the run is a failed run, not a shutdown; letting it escape would stop the host.
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Booking event publishing run failed; the next run retries");
        }
    }
}
