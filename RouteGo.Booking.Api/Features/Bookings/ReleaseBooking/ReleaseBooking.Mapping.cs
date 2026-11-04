namespace RouteGo.Booking.Api.Features.Bookings.ReleaseBooking;

public sealed partial class ReleaseBookingHandler
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
        RefundRow? refund = await writer.Connection.QuerySingleOrDefaultAsync<RefundRow>(new CommandDefinition(
            SelectRefundSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        return ToBooking(row, seats.ToList(), refund, now);
    }

    private static async Task<HeadRow?> GetHeadAsync(IWriterConnection writer, Guid bookingId, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(
            SelectHeadSql, new { BookingId = bookingId }, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static Booking ToBooking(BookingRow row, IReadOnlyList<BookingSeatRow> seatRows, RefundRow? refund, DateTimeOffset now)
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
            Refund = ToRefund(refund),
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

    private static BookingRefund? ToRefund(RefundRow? row)
    {
        if (row is null)
        {
            return null;
        }

        return new BookingRefund
        {
            Reason = Enum.Parse<RefundReason>(row.Reason),
            Amount = row.Amount,
            Status = Enum.Parse<RefundStatus>(row.Status),
            RequestedAt = UtcTimes.ToOffset(row.RequestedAt),
            CompletedAt = UtcTimes.ToOffset(row.CompletedAt),
        };
    }
}
