namespace RouteGo.Booking.Api.Features.TripSnapshots.CreateTripSnapshot;

/// <summary>
/// Creates Booking's copy of a trip from Fleet's TripScheduled event, with all its seats or none. A trip
/// whose details are already known is never changed, so a repeat or a second announcement does nothing.
/// A trip remembered as cancelled before its announcement arrived gets its details and stays cancelled.
/// </summary>
public sealed class CreateTripSnapshotHandler : IEventWorker
{
    private const string AuditAction = "CreateTripSnapshot";
    private const string TripScheduledType = "TripScheduled";

    // The snapshot row is written only while its details are absent: a new trip, or one known so far
    // from an early cancellation. Nothing comes back when the details were already there.
    private const string UpsertTripSql = """
        INSERT INTO trip_snapshots.trip_snapshots AS t
            (trip_id, operator_id, operator_name, operator_email, source_city_name, destination_city_name,
             service_date, departure_at, arrival_at, bus_name, bus_type, is_ac)
        VALUES
            (@TripId, @OperatorId, @OperatorName, @OperatorEmail, @SourceCityName, @DestinationCityName,
             @ServiceDate, @DepartureAt, @ArrivalAt, @BusName, @BusType, @IsAc)
        ON CONFLICT (trip_id) DO UPDATE SET
            operator_id = EXCLUDED.operator_id,
            operator_name = EXCLUDED.operator_name,
            operator_email = EXCLUDED.operator_email,
            source_city_name = EXCLUDED.source_city_name,
            destination_city_name = EXCLUDED.destination_city_name,
            service_date = EXCLUDED.service_date,
            departure_at = EXCLUDED.departure_at,
            arrival_at = EXCLUDED.arrival_at,
            bus_name = EXCLUDED.bus_name,
            bus_type = EXCLUDED.bus_type,
            is_ac = EXCLUDED.is_ac
        WHERE t.operator_id IS NULL
        RETURNING t.trip_id
        """;

    private const string InsertSeatsSql = """
        INSERT INTO trip_snapshots.trip_snapshot_seats (trip_id, seat_no, seat_class, fare)
        SELECT @TripId, s.seat_no, s.seat_class, s.fare
        FROM unnest(@SeatNos, @SeatClasses, @Fares) AS s(seat_no, seat_class, fare)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ILogger<CreateTripSnapshotHandler> _logger;

    public CreateTripSnapshotHandler(IWriterConnectionFactory writerFactory, ILogger<CreateTripSnapshotHandler> logger)
    {
        _writerFactory = writerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string EventType
    {
        get
        {
            return TripScheduledType;
        }
    }

    /// <summary>
    /// Applies one TripScheduled. Applied covers a trip created now and a repeat for a trip already known;
    /// Rejected is data that can never make a snapshot, naming what is wrong.
    /// </summary>
    public async Task<EventOutcome> HandleAsync(ReceivedEvent receivedEvent, CancellationToken cancellationToken)
    {
        TripScheduledMessage? message = TripScheduledReader.Read(receivedEvent.Data, out string? problem);
        if (message is null)
        {
            return EventOutcome.Rejected($"TripScheduled event {receivedEvent.EventId} cannot make a trip snapshot: {problem}");
        }

        bool isCreated = await SaveAsync(message, cancellationToken);
        if (!isCreated)
        {
            _logger.LogInformation(
                "Trip {TripId} already has its details; TripScheduled {EventId} ignored (correlation {CorrelationId})",
                message.TripId, receivedEvent.EventId, receivedEvent.CorrelationId);
            return EventOutcome.Applied();
        }

        _logger.LogInformation(
            "Trip snapshot {TripId} created with {SeatCount} seats (correlation {CorrelationId})",
            message.TripId, message.Seats.Count, receivedEvent.CorrelationId);
        return EventOutcome.Applied();
    }

    private async Task<bool> SaveAsync(TripScheduledMessage message, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        Guid? created = await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            UpsertTripSql, message, writer.Transaction, cancellationToken: cancellationToken));
        if (created is null)
        {
            return false;
        }

        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertSeatsSql, ToSeatParameters(message), writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
        return true;
    }

    private static object ToSeatParameters(TripScheduledMessage message)
    {
        string[] seatNos = message.Seats.Select(seat => seat.SeatNo).ToArray();
        string[] seatClasses = message.Seats.Select(seat => seat.SeatClass).ToArray();
        long[] fares = message.Seats.Select(seat => seat.Fare).ToArray();
        return new { message.TripId, SeatNos = seatNos, SeatClasses = seatClasses, Fares = fares };
    }
}
