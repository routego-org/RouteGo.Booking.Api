namespace RouteGo.Booking.Api.Common.Messaging;

/// <summary>
/// One slice's worker for one event type. Several slices can read the same queue (Payment's results
/// are three capabilities on one queue), so the queue service dispatches each event to the worker
/// that names its type, and the slices never call each other.
/// </summary>
public interface IEventWorker
{
    /// <summary>The event type this worker applies.</summary>
    string EventType { get; }

    /// <summary>Applies one event and says what should happen to its queue message.</summary>
    /// <param name="receivedEvent">The event, read from the RouteGo envelope.</param>
    /// <param name="cancellationToken">Cancelled when the host is stopping.</param>
    Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken);
}
