# CAP-BKG-008 — Apply payment succeeded

- Capability: CAP-BKG-008
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/payment-results.md` (v0.1.3)
- Area: Bookings
- Slice: `Features/Bookings/ApplyPaymentSucceeded/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: payment-succeeded`, event type `PaymentSucceeded`, dispatched by the queue
service shared with CAP-BKG-009 and CAP-CXL-005 (`ApplyPaymentSucceededHandler : IEventWorker`).

- Event data (Payment's wire): `bookingId`, `paymentId`, `amount` (JSON number, read as `decimal?` then required whole and >= 1), `currency` (ignored).
- Reads: `bookings.bookings` (trip_id, status, total_amount). Calls `ISeatInventoryClient.ConfirmSeatsAsync` with no transaction open.
- Writes: `bookings.bookings` (status, payment_id, confirmed_at, failure_reason), `cancellations.refunds`, `bookings.booking_events`.
- Emits: BookingConfirmed; BookingFailed then RefundRequested; RefundRequested alone for a late payment.
- Worker alerts (by the queue service): `invalidEventData`, `bookingUnknown`, `amountMismatch`; `seatInventoryUnavailable` and `bookingNotWaiting` are postponed (retried).

## Acceptance criteria

- [x] AC-1: A payment confirms the booking — `HandleAsync_SuccessForAPaymentPendingBooking_ConfirmsItAndRecordsBookingConfirmed`, `HandleAsync_SuccessForASeatsHeldBookingWithAmountAsDecimalNumber_ConfirmsIt`
- [x] AC-2: The same success five times confirms once — `HandleAsync_SameSuccessFiveTimesAndASecondSuccess_ConfirmsOnceWithoutARefund`
- [x] AC-3: Paid after the hold expired — `HandleAsync_SuccessForAnExpiredBooking_KeepsItExpiredAndRequestsALatePaymentRefund`
- [x] AC-4: Paid after abandoning or failing — `HandleAsync_SuccessForAnAbandonedBooking_KeepsItFailedAndRequestsARefund`
- [x] AC-5: Late payment reported twice refunds once — `HandleAsync_LateSuccessReportedTwice_RefundsOnceAndPublishesOnce`
- [x] AC-6: Seats gone — `HandleAsync_SeatInventoryHoldsNoSeats_FailsTheBookingWithSeatsLostAndRefundsInFull`
- [x] AC-7: Trip cancelled meanwhile — `HandleAsync_TripCancelledAtSeatInventory_FailsTheBookingWithTripCancelledAndRefundsInFull`
- [x] AC-8: Seat Inventory unreachable — `HandleAsync_SeatInventoryCannotAnswer_PostponesAndLeavesTheBookingWaiting`, `HandleAsync_SeatInventoryIsBackOnTheRetry_ConfirmsTheBooking`, `HandleAsync_BookingExpiresWhileSeatInventoryConfirms_KeepsItExpiredAndRefundsAsALatePayment`
- [x] AC-9: Booking stopped during a payment — the event waits on the queue and is read on start; covered by the queue's semantics
- [x] AC-10: A success Booking cannot use — `HandleAsync_SuccessForAnUnknownBooking_IsRejectedAsBookingUnknown`, `HandleAsync_AmountDifferentFromTheBookingsTotal_IsRejectedAndNothingChanges`, `HandleAsync_SuccessWithoutAPaymentId_IsRejectedAsInvalidEventData`, `HandleAsync_AmountThatIsNotWhole_IsRejectedAsInvalidEventData`

## Invariants touched

- Every status move is `UPDATE ... WHERE status = ANY(SeatsHeld, PaymentPending)` and checks rows affected; the loser of a race re-reads the status under the row lock and refunds as a late payment.
- A booking has at most one refund (`ON CONFLICT (booking_id) DO NOTHING`); RefundRequested is recorded only when a row was inserted. Refund key `<bookingId>:payment`; the refund's payment_id is the event's paymentId.
- No transaction is open across the Seat Inventory call.
- Events are recorded in the same transaction as the change, after it.
