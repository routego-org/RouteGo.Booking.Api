namespace RouteGo.Booking.Api.Features.Bookings.CreateSeatChange;

// What Booking can know about a seat change itself, before Seat Inventory is asked, and the SQL both phases use.
public sealed partial class CreateSeatChangeHandler
{
    private const string SelectBookingSql = """
        SELECT b.customer_id, b.status, b.trip_id, b.seat_change_count, t.departure_at, t.is_cancelled
        FROM bookings.bookings b
        JOIN trip_snapshots.trip_snapshots t ON t.trip_id = b.trip_id
        WHERE b.booking_id = @BookingId;

        SELECT seat_no, seat_class, fare, passenger_name, passenger_age, passenger_gender
        FROM bookings.booking_seats
        WHERE booking_id = @BookingId
        ORDER BY seat_no;

        SELECT s.seat_no, s.seat_class
        FROM trip_snapshots.trip_snapshot_seats s
        JOIN bookings.bookings b ON b.trip_id = s.trip_id
        WHERE b.booking_id = @BookingId AND s.seat_no = ANY(@SeatNos);
        """;

    // Only a Confirmed booking whose count is still the one read moves on.
    private const string IncrementCountSql = """
        UPDATE bookings.bookings SET seat_change_count = seat_change_count + 1
        WHERE booking_id = @BookingId AND status = 'Confirmed' AND seat_change_count = @SeatChangeCount
        """;

    // The seat label is part of the key, so a swap (4C to 4D and 4D to 4C) cannot be one UPDATE. Each
    // moving seat first takes a place-holder label from its position in the request, then its new label;
    // the passenger, fare and class never leave the row.
    private const string MoveSeatsSql = """
        UPDATE bookings.booking_seats s
        SET seat_no = '~' || m.move_no::text
        FROM unnest(@FromSeatNos, @ToSeatNos) WITH ORDINALITY AS m(from_seat_no, to_seat_no, move_no)
        WHERE s.booking_id = @BookingId AND s.seat_no = m.from_seat_no;

        UPDATE bookings.booking_seats s
        SET seat_no = m.to_seat_no
        FROM unnest(@FromSeatNos, @ToSeatNos) WITH ORDINALITY AS m(from_seat_no, to_seat_no, move_no)
        WHERE s.booking_id = @BookingId AND s.seat_no = '~' || m.move_no::text;
        """;

    // The order is the spec's: who and what the booking is, the trip, the cutoff, then a repeat (before the
    // limit and ownership, so a done change stays successful), then the limit and the seats.
    private static CreateSeatChangeResult? Decide(
        LoadedBooking? loaded, SeatChangeTarget target, IReadOnlyList<SeatMove> moves, BookingSettingsValues settings, DateTimeOffset now)
    {
        CreateSeatChangeResult? refusal = DecideBooking(loaded, target, settings, now);
        if (refusal != null)
        {
            return refusal;
        }

        if (AreAllMovesDone(loaded!, moves))
        {
            return AlreadyDone(target.BookingId, loaded!);
        }

        return DecideSeats(loaded!, moves, settings);
    }

    private static CreateSeatChangeResult? DecideBooking(
        LoadedBooking? loaded, SeatChangeTarget target, BookingSettingsValues settings, DateTimeOffset now)
    {
        if (loaded is null)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.NotFound };
        }

        if (loaded.Booking.CustomerId != target.Caller.AccountId)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.NotTheCustomersBooking };
        }

        BookingStatus status = Enum.Parse<BookingStatus>(loaded.Booking.Status);
        if (status != BookingStatus.Confirmed)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.BookingNotConfirmed, BookingStatus = status };
        }

        if (loaded.Booking.IsCancelled)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.TripCancelled };
        }

        DateTimeOffset departureAt = UtcTimes.ToOffset(loaded.Booking.DepartureAt);
        if (BookingRules.IsBookingClosed(departureAt, now, settings.CutoffMinutes))
        {
            return new CreateSeatChangeResult
            {
                Outcome = CreateSeatChangeOutcome.BookingClosed,
                DepartureAt = departureAt,
                CutoffMinutes = settings.CutoffMinutes,
            };
        }

        return null;
    }

    private static CreateSeatChangeResult? DecideSeats(LoadedBooking loaded, IReadOnlyList<SeatMove> moves, BookingSettingsValues settings)
    {
        if (loaded.Booking.SeatChangeCount >= settings.SeatChangeLimit)
        {
            return new CreateSeatChangeResult
            {
                Outcome = CreateSeatChangeOutcome.SeatChangeLimitReached,
                MaxSeatChanges = settings.SeatChangeLimit,
            };
        }

        var ownSeatNos = loaded.Seats.Select(seat => seat.SeatNo).ToHashSet();
        var notOwned = moves.Where(move => !ownSeatNos.Contains(move.FromSeatNo)).Select(move => move.FromSeatNo).ToList();
        if (notOwned.Count > 0)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatsNotInBooking, SeatNos = notOwned };
        }

        var tripClasses = loaded.TripSeats.ToDictionary(seat => seat.SeatNo, seat => seat.SeatClass);
        var unknown = moves.Where(move => !tripClasses.ContainsKey(move.ToSeatNo)).Select(move => move.ToSeatNo).ToList();
        if (unknown.Count > 0)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.UnknownSeats, SeatNos = unknown };
        }

        return DecideClasses(loaded, moves, tripClasses);
    }

    private static CreateSeatChangeResult? DecideClasses(
        LoadedBooking loaded, IReadOnlyList<SeatMove> moves, Dictionary<string, string> tripClasses)
    {
        var bookedClasses = loaded.Seats.ToDictionary(seat => seat.SeatNo, seat => seat.SeatClass);
        var mismatched = new List<SeatMove>();
        foreach (SeatMove move in moves)
        {
            if (bookedClasses[move.FromSeatNo] != tripClasses[move.ToSeatNo])
            {
                mismatched.Add(move);
            }
        }

        if (mismatched.Count > 0)
        {
            return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.SeatClassMismatch, Moves = mismatched };
        }

        return null;
    }

    // Done means the passenger is already on the new seat and no longer on the old one.
    private static bool AreAllMovesDone(LoadedBooking loaded, IReadOnlyList<SeatMove> moves)
    {
        var ownSeatNos = loaded.Seats.Select(seat => seat.SeatNo).ToHashSet();
        foreach (SeatMove move in moves)
        {
            bool isOnNewSeat = ownSeatNos.Contains(move.ToSeatNo);
            bool isOffOldSeat = !ownSeatNos.Contains(move.FromSeatNo);
            if (!isOnNewSeat || !isOffOldSeat)
            {
                return false;
            }
        }

        return true;
    }

    private static CreateSeatChangeResult AlreadyDone(Guid bookingId, LoadedBooking loaded)
    {
        var response = new SeatChange
        {
            BookingId = bookingId,
            Changes = Array.Empty<SeatMove>(),
            SeatChangeCount = loaded.Booking.SeatChangeCount,
            Seats = ToSeats(loaded.Seats),
        };
        return new CreateSeatChangeResult { Outcome = CreateSeatChangeOutcome.AlreadyDone, Response = response };
    }

    private sealed class BookingRow
    {
        public string CustomerId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public Guid TripId { get; init; }

        public int SeatChangeCount { get; init; }

        public DateTime DepartureAt { get; init; }

        public bool IsCancelled { get; init; }
    }

    private sealed class TripSeatRow
    {
        public string SeatNo { get; init; } = string.Empty;

        public string SeatClass { get; init; } = string.Empty;
    }
}
