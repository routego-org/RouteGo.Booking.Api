# CAP-CXL-003 — Force-cancel a booking (Admin)

- Capability: CAP-CXL-003
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/cancellations/features/admin-force-cancellation.md` (v0.1.0)
- Area: Cancellations
- Slice: `Features/Cancellations/CreateForceCancellation/`

## Contract
Implemented verbatim from `contracts/cancellations/cancellation.yaml`.

- Operation: `createForceCancellation`; `POST /bookings/{bookingId}/forceCancellations`; policy `Admin`
- Request: `reason` (10..500), `refundPercent` (optional whole number 0..100; read as `decimal?` so 12.5 reaches validation)
- Response: `201` `Cancellation` with `Location: /bookings/{bookingId}`; `200` for a repeat
- Problems: `VALIDATION_FAILED` 400, `UNAUTHORIZED` 401, `FORBIDDEN` 403, `NOT_FOUND` 404, `BOOKING_NOT_CONFIRMED` 409,
  `FORCE_CANCEL_WINDOW_CLOSED` 409 (`departedAt`, `windowDays`)
- Writes (one transaction, audit action `CreateForceCancellation`): `bookings.bookings`, `cancellations.cancellations` (kind `AdminCancelled`, reason, `cancelled_by` = admin),
  `cancellations.refunds` (only when the amount is more than 0), `bookings.booking_events`.
- Emits, in order: `BookingCancelled` (kind, reason), `RefundRequested` (amount > 0), `AuditRecorded` (action `BookingForceCancelled`, details trip, percent, amount, reason).

## Acceptance criteria
Each maps to tests in `CreateForceCancellation.Tests.cs`.

- [x] AC-1: no percentage, full refund, after departure — `HandleAsync_NoPercentageAfterDeparture_CancelsWithAFullRefund`,
  `HandleAsync_ForceCancel_RecordsCancelledRefundRequestedAndAuditEventsWithTheReason`, `ForceCancel_ConfirmedBooking_ReturnsCreatedWithTheCancellationAndTheBookingsLocation`
- [x] AC-2: a chosen percentage — `HandleAsync_FiftyPercent_RefundsHalfRoundedDown`
- [x] AC-3: 0% gives no refund — `HandleAsync_ZeroPercent_CancelsWithNoRefundRecordedOrRequested`
- [x] AC-4: up to 7 days after departure — `HandleAsync_ExactlySevenDaysAfterDeparture_IsStillAllowed`, `HandleAsync_OneMinutePastSevenDays_IsRefusedWithTheDepartureAndTheWindow`,
  `HandleAsync_WindowSettingChanged_TheNewWindowApplies`, `ForceCancel_DepartedMoreThanSevenDaysAgo_IsRefusedWithItsCodeDepartureAndWindow`
- [x] AC-5: only a confirmed booking — `HandleAsync_PaymentPendingBooking_IsRefusedNamingItsStatus`, `HandleAsync_BookingCancelledByItsCustomer_IsRefusedNotConfirmed`,
  `ForceCancel_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus`
- [x] AC-6: repeat — `HandleAsync_ForceCancellingAgain_ReturnsTheExistingCancellationAndRecordsNothingNew`, `ForceCancel_RepeatThroughTheHost_ReturnsOkWithTheSameCancellation`
- [x] AC-7: request rules — `Validate_ReasonOfTheWrongLength_IsRefusedNamingTheReason`, `Validate_MissingReason_IsRefusedNamingTheReason`,
  `Validate_PercentageThatIsNotAWholeNumberFromZeroToHundred_IsRefused`, `Validate_WholePercentageFromZeroToHundredAndAReasonAtTheLimits_IsAccepted`,
  `ForceCancel_PercentageOfTwelvePointFive_IsRefusedValidationFailedNamingTheField`, `ForceCancel_ShortReason_IsRefusedValidationFailedNamingTheField`
- [x] AC-8: admins only — `ForceCancel_WithoutAnAccessToken_IsRefusedUnauthorized`, `ForceCancel_CallerThatIsNotAnAdmin_IsRefusedForbiddenAndNothingChanges`,
  `ForceCancel_UnknownBooking_IsRefusedNotFoundWithItsCode`, `HandleAsync_UnknownBooking_IsRefusedNotFound`

## Invariants touched
- Check order: not found -> Cancelled (by an admin: 200 existing cancellation, no event; otherwise `BOOKING_NOT_CONFIRMED`) -> other status `BOOKING_NOT_CONFIRMED` ->
  window (`now > departureAt + ForceCancelWindowDays`; exactly at the end is allowed). No other time rule; the refund tiers never apply.
- Percent defaults to `RefundRules.DefaultForceCancelPercent()`; the amount is `RefundRules.CalculateAmount`. The whole-number check lives in the validator, then `RefundRules.IsValidPercent`.
- The booking row is read `FOR UPDATE` so the conditional status move and a repeat see one outcome. Settings are read once per request.

## Known deviation / shared-core request
- The reason length (10..500) has no Domain predicate; the validator keeps two named constants. Proposed `RefundRules.IsValidForceCancelReason(string?)` (see the report).
