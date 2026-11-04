namespace RouteGo.Booking.Api.Common.Events;

/// <summary>
/// Builds the data of each booking event exactly as the booking events page defines it. Pure:
/// it only arranges what the <see cref="BookingEventContext"/> already holds. Enumerated values are
/// PascalCase, amounts are whole rupees, times are UTC.
/// </summary>
public static class BookingEventData
{
    /// <summary>The full ticket snapshot that BookingConfirmed and BookingSeatsChanged carry.</summary>
    public static Dictionary<string, object?> TicketSnapshot(BookingEventContext context)
    {
        var seats = new List<object>();
        foreach (BookingEventSeat seat in context.Seats)
        {
            seats.Add(new
            {
                seatNo = seat.SeatNo,
                seatClass = seat.SeatClass,
                fare = seat.Fare,
                name = seat.PassengerName,
                age = seat.PassengerAge,
                gender = seat.PassengerGender,
            });
        }

        return new Dictionary<string, object?>
        {
            ["bookingId"] = context.BookingId,
            ["customerId"] = context.CustomerId,
            ["contact"] = Contact(context),
            ["operator"] = new { operatorId = context.OperatorId, name = context.OperatorName, email = context.OperatorEmail },
            ["trip"] = new
            {
                tripId = context.TripId,
                serviceDate = context.ServiceDate,
                source = context.SourceCityName,
                destination = context.DestinationCityName,
                departureUtc = context.DepartureAt,
                arrivalUtc = context.ArrivalAt,
                busName = context.BusName,
                busType = context.BusType,
                isAc = context.IsAc,
            },
            ["seats"] = seats,
            ["totalAmount"] = context.TotalAmount,
            ["currency"] = BookingEventTypes.Currency,
        };
    }

    /// <summary>BookingConfirmed: the ticket snapshot, the payment and the moment of confirmation.</summary>
    public static Dictionary<string, object?> Confirmed(BookingEventContext context)
    {
        Dictionary<string, object?> data = TicketSnapshot(context);
        data["paymentId"] = context.PaymentId;
        data["confirmedAt"] = context.ConfirmedAt;
        return data;
    }

    /// <summary>BookingSeatsChanged: the ticket snapshot after the change, the moves and the new count.</summary>
    public static Dictionary<string, object?> SeatsChanged(BookingEventContext context, IReadOnlyList<SeatMove> moves)
    {
        var changes = new List<object>();
        foreach (SeatMove move in moves)
        {
            changes.Add(new { fromSeatNo = move.FromSeatNo, toSeatNo = move.ToSeatNo });
        }

        Dictionary<string, object?> data = TicketSnapshot(context);
        data["changes"] = changes;
        data["seatChangeCount"] = context.SeatChangeCount;
        return data;
    }

    /// <summary>BookingFailed: the booking, the seats it asked for, who to tell, the route and why it failed.</summary>
    public static Dictionary<string, object?> Failed(BookingEventContext context, FailureReason reason)
    {
        Dictionary<string, object?> data = EndedBooking(context);
        data["reason"] = reason.ToString();
        return data;
    }

    /// <summary>BookingExpired: the booking, the seats that were held, who to tell and the route.</summary>
    public static Dictionary<string, object?> Expired(BookingEventContext context)
    {
        return EndedBooking(context);
    }

    /// <summary>BookingCancelled: the booking, its seats, who to tell, the route, who cancelled and the refund.</summary>
    public static Dictionary<string, object?> Cancelled(BookingEventContext context, CancelledEventInfo info)
    {
        Dictionary<string, object?> data = EndedBooking(context);
        data["kind"] = info.Kind;
        if (info.Reason is not null)
        {
            data["reason"] = info.Reason;
        }

        data["refundPercent"] = info.RefundPercent;
        data["refundAmount"] = info.RefundAmount;
        data["currency"] = BookingEventTypes.Currency;
        return data;
    }

    /// <summary>RefundRequested: the payment to refund, the amount, why, and the key that makes a repeat harmless.</summary>
    public static Dictionary<string, object?> RefundRequested(Guid bookingId, Guid paymentId, long amount, RefundReason reason)
    {
        string key = RefundRules.PaymentKey(bookingId);
        if (reason == RefundReason.Cancellation)
        {
            key = RefundRules.CancellationKey(bookingId);
        }

        return new Dictionary<string, object?>
        {
            ["bookingId"] = bookingId,
            ["paymentId"] = paymentId,
            ["amount"] = amount,
            ["currency"] = BookingEventTypes.Currency,
            ["reason"] = reason.ToString(),
            ["idempotencyKey"] = key,
        };
    }

    /// <summary>AuditRecorded: who did what to which booking, with the details finance reads.</summary>
    public static Dictionary<string, object?> Audit(
        Guid bookingId, string actorId, string actorRole, string action, object details)
    {
        return new Dictionary<string, object?>
        {
            ["actorId"] = actorId,
            ["actorRole"] = actorRole,
            ["action"] = action,
            ["entityType"] = "Booking",
            ["entityId"] = bookingId,
            ["details"] = details,
        };
    }

    private static Dictionary<string, object?> EndedBooking(BookingEventContext context)
    {
        var seatNos = new List<string>();
        foreach (BookingEventSeat seat in context.Seats)
        {
            seatNos.Add(seat.SeatNo);
        }

        return new Dictionary<string, object?>
        {
            ["bookingId"] = context.BookingId,
            ["tripId"] = context.TripId,
            ["seatNos"] = seatNos,
            ["contact"] = Contact(context),
            ["trip"] = new
            {
                source = context.SourceCityName,
                destination = context.DestinationCityName,
                departureUtc = context.DepartureAt,
            },
        };
    }

    private static object Contact(BookingEventContext context)
    {
        return new { name = context.ContactName, email = context.ContactEmail };
    }
}
