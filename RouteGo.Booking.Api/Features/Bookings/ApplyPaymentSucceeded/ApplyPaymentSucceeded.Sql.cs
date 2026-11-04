namespace RouteGo.Booking.Api.Features.Bookings.ApplyPaymentSucceeded;

public sealed partial class ApplyPaymentSucceededHandler
{
    private const string SelectBookingSql = """
        SELECT trip_id, status, total_amount
        FROM bookings.bookings
        WHERE booking_id = @BookingId
        """;

    // Locks the row so the status read here cannot move before the refund decision that follows it.
    private const string SelectStatusSql = """
        SELECT status FROM bookings.bookings WHERE booking_id = @BookingId FOR UPDATE
        """;

    private const string ConfirmSql = """
        UPDATE bookings.bookings
        SET status = 'Confirmed', payment_id = @PaymentId, confirmed_at = @ConfirmedAt
        WHERE booking_id = @BookingId AND status = ANY(@WaitingStatuses)
        """;

    private const string FailSql = """
        UPDATE bookings.bookings
        SET status = 'Failed', failure_reason = @FailureReason
        WHERE booking_id = @BookingId AND status = ANY(@WaitingStatuses)
        """;

    // A booking has at most one refund, so a repeat finds the row and inserts nothing.
    private const string InsertRefundSql = """
        INSERT INTO cancellations.refunds (booking_id, payment_id, reason, amount, refund_key, status, requested_at)
        VALUES (@BookingId, @PaymentId, @Reason, @Amount, @RefundKey, 'Requested', @RequestedAt)
        ON CONFLICT (booking_id) DO NOTHING
        """;
}
