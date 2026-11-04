# CAP-BKG-010 — Expire booking

- Capability: CAP-BKG-010
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/booking-expiry.md` (v0.1.3)
- Area: Bookings
- Slice: `Features/Bookings/ExpireBooking/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: seat-released`, event type `SeatReleased` from Seat Inventory (`ExpireBookingHandler : IEventWorker`).

- Event data (Seat Inventory's wire): `tripId`, `bookingId`, `seatNos`, `reason`, `releasedTo` (ignored). Reasons: `HoldExpired`, `AdminReleased` expire; `HoldReleased`, `BookingFailed`, `BookingCancelled` change nothing; any other value is unusable.
- Writes: `bookings.bookings` (status), `bookings.booking_events`.
- Emits: BookingExpired.
- Worker alerts: `invalidEventData`, `bookingUnknown`.

## Acceptance criteria

- [x] AC-1: The hold ran out — `HandleAsync_HoldExpiredForAPaymentPendingBooking_ExpiresItAndRecordsBookingExpired`
- [x] AC-2: An admin freed the hold — `HandleAsync_AdminReleasedForASeatsHeldBooking_ExpiresIt`
- [x] AC-3: Releases Booking caused — `HandleAsync_ReleaseBookingCausedForAFailedBooking_ChangesNothing`, `HandleAsync_ReleaseBookingCausedForAWaitingBooking_LeavesItWaiting`, `HandleAsync_ReleaseForACancelledBooking_ChangesNothing`
- [x] AC-4: A booking already paid — `HandleAsync_HoldExpiredForAConfirmedBooking_LeavesItConfirmedAndRecordsNothing`
- [x] AC-5: The same release twice — `HandleAsync_SameReleaseTwice_RecordsBookingExpiredOnce`
- [x] AC-6: Then the payment arrives — covered by CAP-BKG-008 (`HandleAsync_SuccessForAnExpiredBooking_KeepsItExpiredAndRequestsALatePaymentRefund`)
- [x] AC-7: A release for an unknown booking — `HandleAsync_ReleaseForAnUnknownBooking_IsRejectedAsBookingUnknown`, `HandleAsync_ReleaseWithAnUnknownReason_IsRejectedAsInvalidEventData`, `HandleAsync_ReleaseWithoutABookingId_IsRejectedAsInvalidEventData`

## Invariants touched

- The move is `UPDATE ... WHERE status = ANY(SeatsHeld, PaymentPending)` with rows affected checked; BookingExpired is recorded in the same transaction.
