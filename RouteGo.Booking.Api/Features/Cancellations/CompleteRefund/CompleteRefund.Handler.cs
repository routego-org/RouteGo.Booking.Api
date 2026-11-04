namespace RouteGo.Booking.Api.Features.Cancellations.CompleteRefund;

/// <summary>
/// Records a booking's refund as completed when Payment reports the money returned. Only a refund
/// that is still Requested and whose amount equals the reported amount changes; a repeat finds it
/// Completed and changes nothing. Completing a refund publishes nothing.
/// </summary>
public sealed class CompleteRefundHandler : IEventWorker
{
    private const string AuditAction = "CompleteRefund";
    private const string RefundCompletedType = "RefundCompleted";
    private const string SelectRefundSql = """
        SELECT amount, status
        FROM cancellations.refunds
        WHERE booking_id = @BookingId
        FOR UPDATE
        """;

    private const string CompleteRefundSql = """
        UPDATE cancellations.refunds
        SET status = 'Completed', completed_at = @CompletedAt
        WHERE booking_id = @BookingId AND status = 'Requested'
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly ILogger<CompleteRefundHandler> _logger;

    public CompleteRefundHandler(IWriterConnectionFactory writerFactory, IClock clock, ILogger<CompleteRefundHandler> logger)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EventType
    {
        get { return RefundCompletedType; }
    }

    /// <summary>
    /// Applies one RefundCompleted event. Applied covers a refund completed now and a repeat; Rejected
    /// is a completion that matches no refund, names a different amount or cannot be read.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        RefundCompletedData? data = TryRead(receivedEvent.Data);
        bool hasBooking = Guid.TryParse(data?.BookingId, out Guid bookingId);
        long? amount = WholeAmount(data?.Amount);
        if (!hasBooking || amount is null)
        {
            return EventOutcome.Rejected(
                $"RefundCompleted event {receivedEvent.EventId} needs a bookingId and a whole-rupee amount of 1 or more");
        }

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        RefundRow? refund = await writer.Connection.QuerySingleOrDefaultAsync<RefundRow>(new CommandDefinition(
            SelectRefundSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
        EventOutcome? refusal = Check(refund, amount.Value, bookingId, receivedEvent.EventId);
        if (refusal != null)
        {
            return refusal;
        }

        if (refund!.Status == nameof(RefundStatus.Requested))
        {
            var completion = new { BookingId = bookingId, CompletedAt = _clock.UtcNow };
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                CompleteRefundSql, completion, writer.Transaction, cancellationToken: cancellationToken));
            await writer.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Refund of booking {BookingId} completed for {Amount} (correlation {CorrelationId})",
                bookingId, amount.Value, receivedEvent.CorrelationId);
        }

        return EventOutcome.Applied();
    }

    private static EventOutcome? Check(RefundRow? refund, long amount, Guid bookingId, string eventId)
    {
        if (refund is null)
        {
            return EventOutcome.Rejected("refundUnknown", $"event {eventId}: booking {bookingId} has no refund, or does not exist");
        }

        if (refund.Amount != amount)
        {
            return EventOutcome.Rejected(
                "amountMismatch", $"event {eventId}: booking {bookingId} has a refund of {refund.Amount}, Payment reports {amount}");
        }

        return null;
    }

    // Data that cannot be read at all is unusable like any other; the handler names it.
    private static RefundCompletedData? TryRead(JsonElement data)
    {
        try
        {
            return data.Deserialize<RefundCompletedData>(WireJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? WholeAmount(decimal? amount)
    {
        if (amount is null)
        {
            return null;
        }

        bool isWhole = amount.Value == decimal.Truncate(amount.Value);
        bool isInRange = amount.Value >= 1 && amount.Value <= long.MaxValue;
        if (!isWhole || !isInRange)
        {
            return null;
        }

        return (long)amount.Value;
    }

    private sealed class RefundRow
    {
        public long Amount { get; init; }

        public string Status { get; init; } = string.Empty;
    }
}
