namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

public sealed partial class CreateBookingHandler
{
    // Checks run in the order the customer would want to hear about them, and the last check, whether
    // the customer already has an unpaid booking, is the insert itself so two requests cannot both pass.
    private async Task<CreateBookingResult?> RecordAsync(
        NewBooking booking, BookingSettingsValues settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var tripParameters = new { booking.TripId };
        TripRow? trip = await writer.Connection.QuerySingleOrDefaultAsync<TripRow>(new CommandDefinition(
            SelectTripSql, tripParameters, writer.Transaction, cancellationToken: cancellationToken));
        CreateBookingResult? refusal = CheckTrip(trip, settings, now);
        if (refusal != null)
        {
            return refusal;
        }

        var seatParameters = new { booking.TripId, SeatNos = booking.SeatNos.ToArray() };
        IEnumerable<SeatPriceRow> found = await writer.Connection.QueryAsync<SeatPriceRow>(new CommandDefinition(
            SelectSeatPricesSql, seatParameters, writer.Transaction, cancellationToken: cancellationToken));
        var prices = found.ToDictionary(price => price.SeatNo, StringComparer.Ordinal);
        refusal = CheckSeats(booking, prices);
        if (refusal != null)
        {
            return refusal;
        }

        bool isRecorded = await InsertBookingAsync(writer, booking, prices, cancellationToken);
        if (!isRecorded)
        {
            return await GetUnpaidRefusalAsync(writer, booking.CustomerId, cancellationToken);
        }

        await writer.CommitAsync(cancellationToken);
        return null;
    }

    // A trip remembered only from an early cancellation has no details and cannot be booked: it is
    // reported as unknown, like a trip Fleet has not announced.
    private static CreateBookingResult? CheckTrip(TripRow? trip, BookingSettingsValues settings, DateTimeOffset now)
    {
        if (trip is null || trip.OperatorId is null)
        {
            return CreateBookingResult.Refused(CreateBookingOutcome.TripNotFound);
        }

        if (trip.IsCancelled)
        {
            return CreateBookingResult.Refused(CreateBookingOutcome.TripCancelled);
        }

        DateTimeOffset departureAt = UtcTimes.ToOffset(trip.DepartureAt!.Value);
        if (BookingRules.IsBookingClosed(departureAt, now, settings.CutoffMinutes))
        {
            return new CreateBookingResult
            {
                Outcome = CreateBookingOutcome.BookingClosed,
                DepartureAt = departureAt,
                CutoffMinutes = settings.CutoffMinutes,
            };
        }

        return null;
    }

    private static CreateBookingResult? CheckSeats(NewBooking booking, Dictionary<string, SeatPriceRow> prices)
    {
        var unknown = new List<string>();
        var seatFares = new List<SeatFareProblemItem>();
        long currentTotal = 0;
        foreach (NewSeat seat in booking.Seats)
        {
            if (!prices.TryGetValue(seat.SeatNo, out SeatPriceRow? price))
            {
                unknown.Add(seat.SeatNo);
                continue;
            }

            currentTotal += price.Fare;
            seatFares.Add(new SeatFareProblemItem(seat.SeatNo, (int)price.Fare));
        }

        if (unknown.Count > 0)
        {
            return new CreateBookingResult { Outcome = CreateBookingOutcome.UnknownSeats, SeatNos = unknown };
        }

        if (currentTotal != booking.ExpectedTotal)
        {
            return new CreateBookingResult
            {
                Outcome = CreateBookingOutcome.PriceChanged,
                CurrentTotal = currentTotal,
                SeatFares = seatFares,
            };
        }

        return null;
    }

    private static async Task<bool> InsertBookingAsync(
        IWriterConnection writer, NewBooking booking, Dictionary<string, SeatPriceRow> prices, CancellationToken cancellationToken)
    {
        var bookingParameters = new
        {
            booking.BookingId,
            booking.CustomerId,
            booking.ContactName,
            booking.ContactEmail,
            booking.TripId,
            TotalAmount = booking.ExpectedTotal,
        };
        int inserted = await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertBookingSql, bookingParameters, writer.Transaction, cancellationToken: cancellationToken));
        if (inserted == 0)
        {
            return false;
        }

        // Class and fare come from the very rows the price check summed, so the stored seats add up to the total.
        var seatParameters = new
        {
            booking.BookingId,
            SeatNos = booking.Seats.Select(seat => seat.SeatNo).ToArray(),
            SeatClasses = booking.Seats.Select(seat => prices[seat.SeatNo].SeatClass).ToArray(),
            Fares = booking.Seats.Select(seat => prices[seat.SeatNo].Fare).ToArray(),
            Names = booking.Seats.Select(seat => seat.PassengerName).ToArray(),
            Ages = booking.Seats.Select(seat => seat.PassengerAge).ToArray(),
            Genders = booking.Seats.Select(seat => seat.PassengerGender).ToArray(),
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertSeatsSql, seatParameters, writer.Transaction, cancellationToken: cancellationToken));
        return true;
    }

    private static async Task<CreateBookingResult> GetUnpaidRefusalAsync(
        IWriterConnection writer, string customerId, CancellationToken cancellationToken)
    {
        UnpaidBookingRow? existing = await writer.Connection.QuerySingleOrDefaultAsync<UnpaidBookingRow>(new CommandDefinition(
            SelectUnpaidBookingSql, new { CustomerId = customerId }, writer.Transaction, cancellationToken: cancellationToken));
        if (existing is null)
        {
            // The other booking ended between the insert and this read; the customer simply asks again.
            return CreateBookingResult.Refused(CreateBookingOutcome.StateChanged);
        }

        return new CreateBookingResult
        {
            Outcome = CreateBookingOutcome.UnpaidBookingExists,
            ExistingBookingId = existing.BookingId,
            HoldExpiresAt = UtcTimes.ToOffset(existing.HoldExpiresAt),
        };
    }
}
