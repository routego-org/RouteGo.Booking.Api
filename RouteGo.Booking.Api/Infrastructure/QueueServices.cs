namespace RouteGo.Booking.Api.Infrastructure;

/// <summary>Builds the background service that reads one event queue and hands each event to the worker that names its type.</summary>
public static class QueueServices
{
    /// <summary>The queue service for these workers; the queue accepts exactly the event types the workers apply.</summary>
    public static EventWorkerQueueService Create(
        IServiceProvider provider, string queueUrl, string deadLetterQueueUrl, ProblemType notAppliedProblem, IReadOnlyList<IEventWorker> workers)
    {
        var eventTypes = new List<string>();
        foreach (IEventWorker worker in workers)
        {
            eventTypes.Add(worker.EventType);
        }

        var queue = new EventQueue
        {
            QueueUrl = queueUrl,
            DeadLetterQueueUrl = deadLetterQueueUrl,
            EventTypes = eventTypes,
            NotAppliedProblem = notAppliedProblem,
        };
        return new EventWorkerQueueService(
            provider.GetRequiredService<IAmazonSQS>(), queue, workers, provider.GetRequiredService<ILogger<EventWorkerQueueService>>());
    }
}
