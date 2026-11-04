namespace RouteGo.Booking.Api.Features.Bookings.CreateSeatChange;

/// <summary>
/// Moves some or all of a confirmed booking's seats to free seats of the same class. Booking checks what it
/// can know itself first (status, trip, cutoff, limit, ownership, seat class), then asks Seat Inventory,
/// outside any transaction, to move the seats all together or not at all. Only when Seat Inventory has
/// moved them does Booking record, in one transaction, each passenger and fare on the new seat, the new
/// seat-change count and the BookingSeatsChanged event. A request whose moves are all already done succeeds
/// without changing anything.
/// </summary>
public sealed partial class CreateSeatChangeHandler
{
    private const string AuditAction = "CreateSeatChange";

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ISeatInventoryClient _seatInventory;
    private readonly IBookingSettings _settings;
    private readonly IClock _clock;

    public CreateSeatChangeHandler(
        IWriterConnectionFactory writerFactory, ISeatInventoryClient seatInventory, IBookingSettings settings, IClock clock)
    {
        _writerFactory = writerFactory;
        _seatInventory = seatInventory;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>Changes the booking's seats as asked, or says why it cannot; a change already done succeeds with no moves.</summary>
    public async Task<CreateSeatChangeResult> HandleAsync(
        SeatChangeTarget target, IReadOnlyList<SeatMove> moves, CancellationToken cancellationToken)
    {
        BookingSettingsValues settings = _settings.Current;
        DateTimeOffset now = _clock.UtcNow;
        LoadedBooking? loaded = await LoadAsync(target.BookingId, moves, cancellationToken);
        CreateSeatChangeResult? decided = Decide(loaded, target, moves, settings, now);
        if (decided != null)
        {
            return decided;
        }

        ChangeSeatsResult answer = await _seatInventory.ChangeSeatsAsync(
            loaded!.Booking.TripId, target.BookingId, moves, target.CorrelationId, cancellationToken);
        if (answer.Outcome != ChangeSeatsOutcome.Changed)
        {
            return RefuseFor(answer);
        }

        return await RecordAsync(target, loaded, moves, cancellationToken);
    }

    // Reading what gates the change runs in a write transaction that is never committed, so the answer is
    // never a replica's stale one.
    private async Task<LoadedBooking?> LoadAsync(Guid bookingId, IReadOnlyList<SeatMove> moves, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var parameters = new { BookingId = bookingId, SeatNos = NamedSeatNos(moves) };
        await using SqlMapper.GridReader grid = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectBookingSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        BookingRow? booking = await grid.ReadSingleOrDefaultAsync<BookingRow>();
        IEnumerable<BookingEventSeat> bookingSeats = await grid.ReadAsync<BookingEventSeat>();
        IEnumerable<TripSeatRow> tripSeats = await grid.ReadAsync<TripSeatRow>();
        if (booking is null)
        {
            return null;
        }

        return new LoadedBooking(booking, bookingSeats.ToList(), tripSeats.ToList());
    }

    private static string[] NamedSeatNos(IReadOnlyList<SeatMove> moves)
    {
        var names = new List<string>();
        foreach (SeatMove move in moves)
        {
            names.Add(move.FromSeatNo);
            names.Add(move.ToSeatNo);
        }

        return names.Distinct().ToArray();
    }

    // Seat Inventory has moved the seats, so the booking must follow; the count and status are checked again
    // in the same statement that changes them, in case the booking changed while Seat Inventory worked.
    private async Task<CreateSeatChangeResult> RecordAsync(
        SeatChangeTarget target, LoadedBooking loaded, IReadOnlyList<SeatMove> moves, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var count = new { target.BookingId, loaded.Booking.SeatChangeCount };
        int countsUpdated = await writer.Connection.ExecuteAsync(new CommandDefinition(
            IncrementCountSql, count, writer.Transaction, cancellationToken: cancellationToken));
        if (countsUpdated == 0)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.BookingChangedMeanwhile };
        }

        var seatMoves = new
        {
            target.BookingId,
            FromSeatNos = moves.Select(move => move.FromSeatNo).ToArray(),
            ToSeatNos = moves.Select(move => move.ToSeatNo).ToArray(),
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            MoveSeatsSql, seatMoves, writer.Transaction, cancellationToken: cancellationToken));
        BookingEventContext context = await BookingEventContextLoader.LoadAsync(writer, target.BookingId, cancellationToken);
        var recorder = new BookingEventRecorder(writer, target.CorrelationId);
        await recorder.RecordSeatsChangedAsync(context, moves, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        var response = new SeatChange
        {
            BookingId = target.BookingId,
            Changes = moves,
            SeatChangeCount = context.SeatChangeCount,
            Seats = ToSeats(context.Seats),
        };
        return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.Changed, Response = response };
    }

    private static CreateSeatChangeResult RefuseFor(ChangeSeatsResult answer)
    {
        switch (answer.Outcome)
        {
            case ChangeSeatsOutcome.SeatsUnavailable:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatsUnavailable, SeatNos = answer.NamedSeats };
            case ChangeSeatsOutcome.UnknownSeats:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.UnknownSeats, SeatNos = answer.NamedSeats };
            case ChangeSeatsOutcome.SeatsNotOwned:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatsNotInBooking, SeatNos = answer.NamedSeats };
            case ChangeSeatsOutcome.SeatClassMismatch:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatClassMismatch, Moves = answer.NamedMoves };
            case ChangeSeatsOutcome.TripCancelled:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.TripCancelled };
            case ChangeSeatsOutcome.TimedOut:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.UpstreamTimeout };
            case ChangeSeatsOutcome.Unavailable:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.UpstreamUnavailable, IsRetryable = true };
            default:
                return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.UpstreamUnavailable, IsRetryable = false };
        }
    }

    private static List<BookingSeat> ToSeats(IReadOnlyList<BookingEventSeat> seats)
    {
        var result = new List<BookingSeat>();
        foreach (BookingEventSeat seat in seats)
        {
            result.Add(new BookingSeat
            {
                SeatNo = seat.SeatNo,
                SeatClass = seat.SeatClass,
                Fare = seat.Fare,
                PassengerName = seat.PassengerName,
                PassengerAge = seat.PassengerAge,
                PassengerGender = Enum.Parse<PassengerGender>(seat.PassengerGender),
            });
        }

        return result;
    }

    /// <summary>What the checks need: the booking with its trip's times, its seats and the named seats of the trip.</summary>
    /// <param name="Booking">The booking and its trip snapshot's state.</param>
    /// <param name="Seats">The booking's seats, by seat label.</param>
    /// <param name="TripSeats">The trip snapshot's seats among those the moves name.</param>
    private sealed record LoadedBooking(BookingRow Booking, IReadOnlyList<BookingEventSeat> Seats, IReadOnlyList<TripSeatRow> TripSeats);
}
