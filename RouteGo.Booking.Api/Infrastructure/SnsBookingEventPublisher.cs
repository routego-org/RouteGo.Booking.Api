using Amazon.SimpleNotificationService.Model;

namespace RouteGo.Booking.Api.Infrastructure;

/// <summary>
/// Publishes Booking events to the booking events stream or the audit events stream (SNS topics),
/// with the event type as the message attribute subscription filter policies match on.
/// Immutable and thread-safe.
/// </summary>
public sealed class SnsBookingEventPublisher : IBookingEventPublisher
{
    private const string EventTypeAttribute = "eventType";
    private const string StringDataType = "String";

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _bookingEventsTopicArn;
    private readonly string _auditEventsTopicArn;

    public SnsBookingEventPublisher(IAmazonSimpleNotificationService sns, string bookingEventsTopicArn, string auditEventsTopicArn)
    {
        _sns = sns;
        _bookingEventsTopicArn = bookingEventsTopicArn;
        _auditEventsTopicArn = auditEventsTopicArn;
    }

    /// <inheritdoc />
    public async Task PublishAsync(bool isAuditEvent, string eventType, string envelopeJson, CancellationToken cancellationToken)
    {
        string topicArn = _bookingEventsTopicArn;
        if (isAuditEvent)
        {
            topicArn = _auditEventsTopicArn;
        }

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = envelopeJson,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [EventTypeAttribute] = new MessageAttributeValue { DataType = StringDataType, StringValue = eventType },
            },
        };
        await _sns.PublishAsync(request, cancellationToken);
    }
}
