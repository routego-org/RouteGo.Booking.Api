# CAP-BKG-004 — Abandon an unpaid booking

- Capability: CAP-BKG-004
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/booking-abandonment.md` (v0.1.3)
- Area: Bookings
- Slice: `Features/Bookings/ReleaseBooking/`

## Contract
Implemented **verbatim** from the issued contract.

- Operation: `releaseBooking` — `contracts/bookings/booking.yaml`
- Route / verb: `POST /bookings/{bookingId}/release`, role Customer (`Policies.Customer`)
- Response: `200` with `Booking` (failed, failure reason abandoned)
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404), `BOOKING_NOT_UNPAID` (409, `bookingStatus` camelCase)
- Reads: `bookings.bookings`, `bookings.booking_seats`, `trip_snapshots.trip_snapshots`, `cancellations.refunds`
- Writes: `bookings.bookings` (status move), `bookings.booking_events`
- Calls: `ISeatInventoryClient.ReleaseSeatsAsync` after the commit, best effort
- Emits (events): one `BookingFailed` (reason `Abandoned`) with the contact and the trip, in the same transaction as the move.

## Acceptance criteria
Each criterion maps to tests in `ReleaseBooking.Tests.cs` and `ReleaseBooking.Http.Tests.cs`.

- [x] AC-1: Abandon frees the seats at once → `HandleAsync_UnpaidBooking_EndsItFailedAbandonedAndAnswersWithIt`, `HandleAsync_UnpaidBooking_ReleasesTheHoldAndRecordsBookingFailedWithContactAndTrip`, `HandleAsync_UnpaidBooking_AsksSeatInventoryToReleaseOnlyAfterTheAbandonIsCommitted`, `ReleaseBooking_OwnUnpaidBooking_Returns200WithTheAbandonedBooking`
- [x] AC-2: Abandon twice → `HandleAsync_AbandonedTwice_AnswersTheSameAndReleasesAndRecordsNothingMore`, `HandleAsync_AbandonedBookingWithALatePaymentRefund_AnswersWithTheRefund`
- [x] AC-3: Paid booking → `HandleAsync_ConfirmedBooking_IsRefusedNotUnpaidNamingConfirmedAndChangesNothing`, `ReleaseBooking_ConfirmedBooking_Returns409BookingNotUnpaidWithItsStatus`
- [x] AC-4: Expired or failed booking → `HandleAsync_ExpiredOrOtherwiseFailedBooking_IsRefusedNotUnpaidNamingItsStatus`
- [~] AC-5: Late payment after abandoning is refunded — not this slice (payment-results workers); this slice only leaves the booking Failed/Abandoned
- [x] AC-6: Seat Inventory unreachable → `HandleAsync_SeatInventoryUnreachable_StillEndsTheBookingAndRecordsTheEvent`
- [x] AC-7: Only the booking's customer → `HandleAsync_AnotherCustomersBooking_IsRefusedForbiddenAndChangesNothing`, `HandleAsync_UnknownBooking_IsRefusedNotFound`, `ReleaseBooking_NotTheBookingsCustomer_IsRefusedForbiddenAndReleasesNothing`, `ReleaseBooking_UnknownBooking_IsRefusedNotFound`, `ReleaseBooking_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched
- The move is `UPDATE ... WHERE status IN ('SeatsHeld','PaymentPending')`; zero rows re-reads the booking and answers for what it now is
  (already abandoned: unchanged; otherwise BOOKING_NOT_UNPAID).
- An `Initiated` booking is refused as not unpaid: only `SeatsHeld` and `PaymentPending` may be abandoned (rule 3).
- The spec requires 403 for someone else's booking and 404 for an unknown one, so the owner is loaded with the booking and decided in the
  handler (the contract overrides the generic out-of-scope-is-404 rule).
- The Seat Inventory release is asked for after the commit and its outcome is ignored: the recorded BookingFailed frees the seats if it is lost.
- A repeat on an abandoned booking releases nothing and records nothing.
