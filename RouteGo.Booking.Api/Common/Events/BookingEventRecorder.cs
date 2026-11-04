using System.Text.Json.Serialization;

namespace RouteGo.Booking.Api.Common.Events;

/// <summary>
/// Records the events a booking change causes, in the same transaction as the change, so the change
/// and its events both happen or neither does. The publisher sends them afterwards, in the order
/// recorded and at least once. One recorder serves one transaction and carries that request's or
/// event's correlation ID into every event it records.
/// </summary>
public sealed class BookingEventRecorder
{
    private const string BookingStream = "BookingEvents";
    private const string AuditStream = "AuditEvents";

    private const string InsertEventSql = """
        INSERT INTO bookings.booking_events (event_id, event_type, stream, booking_id, event_data, correlation_id)
        VALUES (@EventId, @EventType, @Stream, @BookingId, @EventData, @CorrelationId)
        """;

    // Null members are left out: an event's optional fields are simply absent, as the events page says.
    private static readonly JsonSerializerOptions _eventJson = CreateEventJson();

    private readonly IWriterConnection _writer;
    private readonly string _correlationId;

    public BookingEventRecorder(IWriterConnection writer, string correlationId)
    {
        _writer = writer;
        _correlationId = correlationId;
    }

    /// <summary>Records BookingConfirmed for a booking whose seats Seat Inventory has just confirmed.</summary>
    public async Task RecordConfirmedAsync(BookingEventContext context, CancellationToken cancellationToken)
    {
        object data = BookingEventData.Confirmed(context);
        await InsertAsync(BookingEventTypes.BookingConfirmed, context.BookingId, data, cancellationToken);
    }

    /// <summary>Records BookingSeatsChanged for a seat change that moved at least one seat.</summary>
    public async Task RecordSeatsChangedAsync(
        BookingEventContext context, IReadOnlyList<SeatMove> moves, CancellationToken cancellationToken)
    {
        object data = BookingEventData.SeatsChanged(context, moves);
        await InsertAsync(BookingEventTypes.BookingSeatsChanged, context.BookingId, data, cancellationToken);
    }

    /// <summary>Records BookingFailed for a booking that ended without being confirmed.</summary>
    public async Task RecordFailedAsync(BookingEventContext context, FailureReason reason, CancellationToken cancellationToken)
    {
        object data = BookingEventData.Failed(context, reason);
        await InsertAsync(BookingEventTypes.BookingFailed, context.BookingId, data, cancellationToken);
    }

    /// <summary>Records BookingExpired for an unpaid booking whose hold Seat Inventory released.</summary>
    public async Task RecordExpiredAsync(BookingEventContext context, CancellationToken cancellationToken)
    {
        object data = BookingEventData.Expired(context);
        await InsertAsync(BookingEventTypes.BookingExpired, context.BookingId, data, cancellationToken);
    }

    /// <summary>Records BookingCancelled for a confirmed booking that was cancelled.</summary>
    public async Task RecordCancelledAsync(
        BookingEventContext context, CancelledEventInfo info, CancellationToken cancellationToken)
    {
        object data = BookingEventData.Cancelled(context, info);
        await InsertAsync(BookingEventTypes.BookingCancelled, context.BookingId, data, cancellationToken);
    }

    /// <summary>Records RefundRequested: Booking asks Payment to return money, once per refund key.</summary>
    public async Task RecordRefundRequestedAsync(
        Guid bookingId, Guid paymentId, long amount, RefundReason reason, CancellationToken cancellationToken)
    {
        object data = BookingEventData.RefundRequested(bookingId, paymentId, amount, reason);
        await InsertAsync(BookingEventTypes.RefundRequested, bookingId, data, cancellationToken);
    }

    /// <summary>Records AuditRecorded, which goes to the audit events stream.</summary>
    public async Task RecordAuditAsync(
        Guid bookingId, Caller actor, string action, object details, CancellationToken cancellationToken)
    {
        object data = BookingEventData.Audit(bookingId, actor.AccountId, actor.Role, action, details);
        await InsertAsync(BookingEventTypes.AuditRecorded, bookingId, data, cancellationToken);
    }

    private static JsonSerializerOptions CreateEventJson()
    {
        var options = new JsonSerializerOptions(WireJson.Options);
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        return options;
    }

    private async Task InsertAsync(string eventType, Guid bookingId, object data, CancellationToken cancellationToken)
    {
        string stream = BookingStream;
        if (eventType == BookingEventTypes.AuditRecorded)
        {
            stream = AuditStream;
        }

        var parameters = new
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Stream = stream,
            BookingId = bookingId,
            EventData = JsonSerializer.Serialize(data, _eventJson),
            CorrelationId = _correlationId,
        };
        await _writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertEventSql, parameters, _writer.Transaction, cancellationToken: cancellationToken));
    }
}
