# CAP-BKG-002 — Get booking

- Capability: CAP-BKG-002
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/booking-status.md` (v0.1.0)
- Area: Bookings
- Slice: `Features/Bookings/GetBooking/`

## Contract

- Operation: `getBooking` — `contracts/bookings/booking.yaml`
- Route / verb: `GET /bookings/{bookingId}`, policy `CustomerOrAdmin`
- Response: `200` `Booking`
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404)
- Reads: `bookings.bookings`, `bookings.booking_seats`, `trip_snapshots.trip_snapshots`, `cancellations.cancellations`, `cancellations.refunds` (reader connection, one round trip, no writes)
- Emits: nothing

## Acceptance criteria

Each criterion maps to tests in `GetBooking.Tests.cs` (handler, real Postgres) and `GetBooking.Http.Tests.cs` (through the host).

- [x] AC-1: unpaid booking shows hold expiry and payment page — `HandleAsync_PaymentPendingBookingWithinHold_ShowsHoldExpiryCheckoutUrlTripSeatsAndTotal`, `GetBooking_OwnerReadsPaymentPendingBooking_Returns200WithTheContractShape`
- [x] AC-2: confirmed booking, no payment page, no hold expiry — `HandleAsync_ConfirmedBooking_ShowsConfirmedAtAndNoHoldExpiryNorCheckoutUrl`
- [x] AC-3: no payment page once the hold ran out — `HandleAsync_PaymentPendingBookingWhoseHoldRanOut_ShowsNoCheckoutUrl`
- [x] AC-4: failure reason, late-payment refund — `HandleAsync_FailedBookingAndExpiredBookingWithLateRefund_ShowFailureReasonAndRefund`
- [x] AC-5: cancellation and refund (Requested then Completed) — `HandleAsync_CancelledBookingWithRequestedRefund_ShowsCancellationAndRefundWithoutCompletion` (Completed is covered by AC-4's refund)
- [x] AC-6: only the booking's customer; operator refused — `HandleAsync_AnotherCustomersBooking_IsForbiddenAndShowsNothing`, `GetBooking_AnotherCustomer_IsRefusedForbidden`, `GetBooking_Operator_IsRefusedForbidden`
- [x] AC-7: admins read any booking, same answer — `HandleAsync_AdminReadsACustomersBooking_SeesTheSameAnswerTheCustomerSees`, `GetBooking_AdminReadsACustomersBooking_Returns200`
- [x] AC-8: unknown booking, no sign-in — `HandleAsync_UnknownBooking_IsNotFoundForACustomerAndForAnAdmin`, `GetBooking_UnknownBooking_IsRefusedNotFound`, `GetBooking_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched

- Unpaid is `BookingRules.IsUnpaid`; the hold expiry is given only while unpaid.
- The payment page is given only while the status is PaymentPending and the hold expiry is after `IClock.UtcNow` (read once per call).
- The failure reason, cancellation and refund are given exactly when the stored rows have them.

## Notes

- Spec-mandated 403: another customer's booking is refused FORBIDDEN, not NOT_FOUND, as the feature spec's
  AC-6 and business rule 2 require. This overrides the coding standard's generic "out-of-scope is 404" rule, so
  the owner is loaded with the booking and the decision is made in the handler (`MayRead`). An unknown booking is
  NOT_FOUND for everyone; an operator is refused by the `CustomerOrAdmin` policy.
- Reads go through the reader port and are eventually consistent by its contract; the Portal's repeated polling
  after a payment relies on that.
