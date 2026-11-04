namespace RouteGo.Booking.Api.Common.Messaging;

/// <summary>
/// Reads one queue for as long as the API runs and hands each event to the worker that names its
/// event type. The queue's subscription already lets only those types through, so an event no worker
/// names is set aside as unusable.
/// </summary>
public sealed class EventWorkerQueueService : EventQueueService
{
    private readonly Dictionary<string, IEventWorker> _workersByEventType;

    public EventWorkerQueueService(IAmazonSQS sqs, EventQueue queue, IReadOnlyList<IEventWorker> workers, ILogger logger)
        : base(sqs, queue, logger)
    {
        _workersByEventType = new Dictionary<string, IEventWorker>();
        foreach (IEventWorker worker in workers)
        {
            _workersByEventType[worker.EventType] = worker;
        }
    }

    protected override async Task<EventOutcome> HandleEventAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        bool hasWorker = _workersByEventType.TryGetValue(receivedEvent.EventType, out IEventWorker? worker);
        if (!hasWorker)
        {
            return EventOutcome.Rejected($"no worker applies {receivedEvent.EventType} events");
        }

        return await worker!.HandleAsync(receivedEvent, cancellationToken);
    }
}
