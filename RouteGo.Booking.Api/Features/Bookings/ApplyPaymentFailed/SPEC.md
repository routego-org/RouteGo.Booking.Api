# CAP-BKG-009 — Apply payment failed

- Capability: CAP-BKG-009
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/payment-results.md` (v0.1.3)
- Area: Bookings
- Slice: `Features/Bookings/ApplyPaymentFailed/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: payment-failed`, event type `PaymentFailed` (`ApplyPaymentFailedHandler : IEventWorker`).

- Event data (Payment's wire): `bookingId`, `paymentId` (ignored), `reason` (text, kept in `payment_failure_detail`, cut to 500 characters).
- Writes: `bookings.bookings` (status, failure_reason, payment_failure_detail), `bookings.booking_events`.
- Emits: BookingFailed (reason PaymentDeclined), so Seat Inventory frees the seats.
- Worker alerts: `invalidEventData`, `bookingUnknown`.

## Acceptance criteria

- [x] AC-11: A failed payment ends the booking — `HandleAsync_FailureForAPaymentPendingBooking_FailsItKeepingPaymentsReasonAndRecordsBookingFailed`, `HandleAsync_FailureForASeatsHeldBooking_FailsIt`
- [x] AC-12: A failure for a booking no longer waiting — `HandleAsync_FailureForAConfirmedBooking_ChangesNothing`, `HandleAsync_FailureForAnExpiredBooking_ChangesNothing`, `HandleAsync_FailureDeliveredTwice_RecordsBookingFailedOnce`
- [x] AC-13: A failure Booking cannot use — `HandleAsync_FailureForAnUnknownBooking_IsRejectedAsBookingUnknown`, `HandleAsync_FailureWithoutABookingId_IsRejectedAsInvalidEventData`

## Invariants touched

- The move is `UPDATE ... WHERE status = ANY(SeatsHeld, PaymentPending)` with rows affected checked; a repeat finds the booking Failed.
- BookingFailed is recorded in the same transaction, after the change, from the booking and its trip snapshot.
