namespace RouteGo.Booking.Api.Features.Bookings.CreateBooking;

public sealed partial class CreateBookingHandler
{
    private const string Currency = "INR";

    private static async Task<Booking> GetBookingAsync(
        IWriterConnection writer, Guid bookingId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var parameters = new { BookingId = bookingId };
        BookingRow row = await writer.Connection.QuerySingleAsync<BookingRow>(new CommandDefinition(
            SelectBookingSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<BookingSeatRow> seats = await writer.Connection.QueryAsync<BookingSeatRow>(new CommandDefinition(
            SelectBookingSeatsSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        return ToBooking(row, seats.ToList(), now);
    }

    private static Booking ToBooking(BookingRow row, IReadOnlyList<BookingSeatRow> seatRows, DateTimeOffset now)
    {
        BookingStatus status = Enum.Parse<BookingStatus>(row.Status);
        DateTimeOffset? holdExpiresAt = UtcTimes.ToOffset(row.HoldExpiresAt);
        return new Booking
        {
            BookingId = row.BookingId,
            Status = status,
            Trip = ToTrip(row),
            ContactName = row.ContactName,
            ContactEmail = row.ContactEmail,
            Seats = seatRows.Select(ToSeat).ToList(),
            TotalAmount = row.TotalAmount,
            Currency = Currency,
            HoldExpiresAt = ShownHoldExpiry(status, holdExpiresAt),
            CheckoutUrl = ShownCheckoutUrl(status, holdExpiresAt, row.CheckoutUrl, now),
            FailureReason = ToFailureReason(row.FailureReason),
            PaymentFailureDetail = row.PaymentFailureDetail,
            SeatChangeCount = row.SeatChangeCount,
            CreatedAt = UtcTimes.ToOffset(row.CreatedAt),
            ConfirmedAt = UtcTimes.ToOffset(row.ConfirmedAt),
            Cancellation = null,
            Refund = null,
        };
    }

    private static DateTimeOffset? ShownHoldExpiry(BookingStatus status, DateTimeOffset? holdExpiresAt)
    {
        if (!BookingRules.IsUnpaid(status))
        {
            return null;
        }

        return holdExpiresAt;
    }

    private static string? ShownCheckoutUrl(BookingStatus status, DateTimeOffset? holdExpiresAt, string? checkoutUrl, DateTimeOffset now)
    {
        if (status != BookingStatus.PaymentPending || holdExpiresAt is null)
        {
            return null;
        }

        if (now >= holdExpiresAt.Value)
        {
            return null;
        }

        return checkoutUrl;
    }

    private static FailureReason? ToFailureReason(string? failureReason)
    {
        if (failureReason is null)
        {
            return null;
        }

        return Enum.Parse<FailureReason>(failureReason);
    }

    private static TripSummary ToTrip(BookingRow row)
    {
        return new TripSummary
        {
            TripId = row.TripId,
            SourceCityName = row.SourceCityName,
            DestinationCityName = row.DestinationCityName,
            ServiceDate = DateOnly.FromDateTime(row.ServiceDate),
            DepartureAt = UtcTimes.ToOffset(row.DepartureAt),
            ArrivalAt = UtcTimes.ToOffset(row.ArrivalAt),
            BusName = row.BusName,
            BusType = Enum.Parse<BusType>(row.BusType),
            OperatorName = row.OperatorName,
        };
    }

    private static BookingSeat ToSeat(BookingSeatRow row)
    {
        return new BookingSeat
        {
            SeatNo = row.SeatNo,
            SeatClass = row.SeatClass,
            Fare = row.Fare,
            PassengerName = row.PassengerName,
            PassengerAge = row.PassengerAge,
            PassengerGender = Enum.Parse<PassengerGender>(row.PassengerGender),
        };
    }
}
