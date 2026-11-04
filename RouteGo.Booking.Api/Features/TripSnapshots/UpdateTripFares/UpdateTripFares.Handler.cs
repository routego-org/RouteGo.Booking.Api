namespace RouteGo.Booking.Api.Features.TripSnapshots.UpdateTripFares;

/// <summary>
/// Applies Fleet's FareChanged to a trip snapshot so the next booking pays the new fares. Only a change
/// that occurred after the last one applied counts, so a late or repeated change does nothing; bookings
/// already made keep the fares they were sold at because their seats hold their own fare. A change for a
/// trip whose details are not known yet is postponed: the queue delivers it again and parks it if the
/// trip never appears.
/// </summary>
public sealed class UpdateTripFaresHandler : IEventWorker
{
    private const string AuditAction = "UpdateTripFares";
    private const string FareChangedType = "FareChanged";
    private const string TripUnknown = "tripUnknown";

    // The row is locked so two fare changes for one trip are decided one after the other.
    private const string LockTripSql = """
        SELECT operator_id IS NOT NULL AS has_details,
               COALESCE(fares_changed_at >= @OccurredAt, false) AS is_newer_applied
        FROM trip_snapshots.trip_snapshots
        WHERE trip_id = @TripId
        FOR UPDATE
        """;

    private const string UpdateTripSql = """
        UPDATE trip_snapshots.trip_snapshots
        SET fares_changed_at = @OccurredAt
        WHERE trip_id = @TripId
        """;

    private const string UpdateSeatsSql = """
        UPDATE trip_snapshots.trip_snapshot_seats AS seat
        SET fare = changed.fare
        FROM unnest(@SeatNos, @Fares) AS changed(seat_no, fare)
        WHERE seat.trip_id = @TripId AND seat.seat_no = changed.seat_no
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ILogger<UpdateTripFaresHandler> _logger;

    public UpdateTripFaresHandler(IWriterConnectionFactory writerFactory, ILogger<UpdateTripFaresHandler> logger)
    {
        _writerFactory = writerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EventType
    {
        get
        {
            return FareChangedType;
        }
    }

    /// <summary>
    /// Applies one FareChanged. Applied covers fares changed now and a change that is not newer than the
    /// last applied; Rejected is unusable data; Postponed is a trip whose details are not known yet.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        FareChangedMessage? message = FareChangedReader.Read(receivedEvent.Data, out string? problem);
        if (message is null)
        {
            return EventOutcome.Rejected($"FareChanged event {receivedEvent.EventId} cannot change fares: {problem}");
        }

        return await DecideAsync(receivedEvent, message, cancellationToken);
    }

    private async Task<EventOutcome> DecideAsync(
        ReceivedEvent receivedEvent, FareChangedMessage message, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var lockParameters = new { message.TripId, OccurredAt = receivedEvent.OccurredAt.ToUniversalTime() };
        TripRow? trip = await writer.Connection.QuerySingleOrDefaultAsync<TripRow>(new CommandDefinition(
            LockTripSql, lockParameters, writer.Transaction, cancellationToken: cancellationToken));
        if (trip is null || !trip.HasDetails)
        {
            return EventOutcome.Postponed(
                TripUnknown, $"FareChanged event {receivedEvent.EventId} is for trip {message.TripId}, whose snapshot does not exist yet");
        }

        if (trip.IsNewerApplied)
        {
            _logger.LogInformation(
                "FareChanged {EventId} for trip {TripId} is not newer than the fares applied; ignored (correlation {CorrelationId})",
                receivedEvent.EventId, message.TripId, receivedEvent.CorrelationId);
            return EventOutcome.Applied();
        }

        await ApplyAsync(writer, message, lockParameters.OccurredAt, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        _logger.LogInformation(
            "Fares of trip {TripId} changed for {SeatCount} seats (correlation {CorrelationId})",
            message.TripId, message.Seats.Count, receivedEvent.CorrelationId);
        return EventOutcome.Applied();
    }

    private static async Task ApplyAsync(
        IWriterConnection writer, FareChangedMessage message, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            UpdateTripSql, new { message.TripId, OccurredAt = occurredAt }, writer.Transaction, cancellationToken: cancellationToken));
        string[] seatNos = message.Seats.Select(seat => seat.SeatNo).ToArray();
        long[] fares = message.Seats.Select(seat => seat.Fare).ToArray();
        var seatParameters = new { message.TripId, SeatNos = seatNos, Fares = fares };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            UpdateSeatsSql, seatParameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private sealed class TripRow
    {
        public bool HasDetails { get; init; }

        public bool IsNewerApplied { get; init; }
    }
}
