namespace RouteGo.Booking.Api.Common.Ports;

/// <summary>Sends one recorded booking event, already wrapped in the RouteGo envelope, to its stream.</summary>
public interface IBookingEventPublisher
{
    /// <summary>Publishes the envelope to the audit events stream or the booking events stream, tagged with its event type.</summary>
    /// <param name="isAuditEvent">True for AuditRecorded, which goes to the audit events stream.</param>
    /// <param name="eventType">The event type, exposed as a message attribute so subscribers can filter on it.</param>
    /// <param name="envelopeJson">The full envelope.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task PublishAsync(bool isAuditEvent, string eventType, string envelopeJson, CancellationToken cancellationToken);
}
