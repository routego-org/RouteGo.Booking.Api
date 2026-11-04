# CAP-CXL-002 — Cancel a booking

- Capability: CAP-CXL-002
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/cancellations/features/booking-cancellation.md` (v0.1.0)
- Area: Cancellations
- Slice: `Features/Cancellations/CancelBooking/`

## Contract
Implemented verbatim from `contracts/cancellations/cancellation.yaml`.

- Operation: `cancelBooking`; `POST /bookings/{bookingId}/cancel`; policy `Customer`; no body
- Response: `200` `Cancellation` (`bookingId`, `kind`, `reason`, `refundPercent`, `refundAmount`, `currency`, `cancelledAt`, `refundStatus`)
- Problems: `UNAUTHORIZED` 401, `FORBIDDEN` 403, `NOT_FOUND` 404, `BOOKING_NOT_CONFIRMED` 409 (`bookingStatus`), `TRIP_DEPARTED` 409 (`departedAt`)
- Writes (one transaction, audit action `CancelBooking`): `bookings.bookings` (Confirmed -> Cancelled), `cancellations.cancellations`,
  `cancellations.refunds` (only when the amount is more than 0), `bookings.booking_events`.
- Emits, in order: `BookingCancelled` (always), `RefundRequested` (amount > 0), `AuditRecorded` (action `BookingCancelled`, details trip, refund percent, refund amount).

## Acceptance criteria
Each maps to tests in `CancelBooking.Tests.cs`.

- [x] AC-4: cancel with a refund — `HandleAsync_ConfirmedBookingDepartingInThirtyHours_CancelsItWithNinetyPercentAndRecordsTheRefund`,
  `HandleAsync_ConfirmedBooking_RecordsCancelledRefundRequestedAndAuditEventsInOrder`, `Cancel_ConfirmedBooking_ReturnsTheCancellationAsTheContractShapesIt`
- [x] AC-5: no refund due — `HandleAsync_BookingDepartingInTwoHours_CancelsItWithNoRefundAndNoRefundEvent`
- [x] AC-6: the tier at the moment of cancelling — `HandleAsync_TwentyThreeHoursFiftyNineMinutesBeforeDeparture_AppliesTheFiftyPercentTier`
- [x] AC-7: repeat — `HandleAsync_CancellingAgain_ReturnsTheSameCancellationAndRecordsNothingNew`, `Cancel_RepeatThroughTheHost_ReturnsTheSameCancellationWithTheSameStatus`
- [x] AC-8: only a confirmed booking — `HandleAsync_PaymentPendingBooking_IsRefusedNamingItsStatusAndNothingChanges`,
  `HandleAsync_BookingCancelledByAnAdmin_IsRefusedNotConfirmedAndNothingChanges`, `Cancel_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus`
- [x] AC-9: no cancelling after departure — `HandleAsync_TripAlreadyDeparted_IsRefusedWithTheDepartureTimeAndStaysConfirmed`, `Cancel_DepartedTrip_IsRefusedWithItsCodeAndDepartureTime`
- [x] AC-10: only the booking's customer — `HandleAsync_AnotherCustomer_IsRefusedAndTheBookingStaysConfirmed`, `HandleAsync_UnknownBooking_IsRefusedNotFound`,
  `Cancel_WithoutAnAccessToken_IsRefusedUnauthorized`, `Cancel_CallerThatIsNotACustomer_IsRefusedForbiddenAndNothingChanges`,
  `Cancel_AnotherCustomersBooking_IsRefusedForbiddenWithItsCode`, `Cancel_UnknownBooking_IsRefusedNotFoundWithItsCode`
- [x] AC-11: changed tiers apply to the next cancellation — `HandleAsync_TiersChangedBetweenTwoCancellations_TheSecondUsesTheNewTiers`

## Invariants touched
- The booking row is read `FOR UPDATE` inside the transaction, so the status move (`WHERE status = 'Confirmed'`) cannot lose a race and a repeat sees one outcome.
- Check order: not found -> not the caller's (403) -> Cancelled (by the customer: 200 same cancellation, `refundStatus` from the refund row, null when none, no event;
  by anyone else: `BOOKING_NOT_CONFIRMED`) -> other status `BOOKING_NOT_CONFIRMED` -> departed `TRIP_DEPARTED`.
- Percent and amount are worked out at the moment of cancelling from `IBookingSettings.Current`, read once per request; the refund key is `RefundRules.CancellationKey`.
- `cancelled_by` is the caller's account id; `reason` is null.

## Known deviation
- Another customer's booking answers `403 FORBIDDEN` (the contract), not 404.
