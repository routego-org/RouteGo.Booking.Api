# CAP-CXL-001 — Preview a booking cancellation

- Capability: CAP-CXL-001
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/cancellations/features/booking-cancellation.md` (v0.1.0)
- Area: Cancellations
- Slice: `Features/Cancellations/PreviewBookingCancellation/`

## Contract
Implemented verbatim from `contracts/cancellations/cancellation.yaml`.

- Operation: `previewBookingCancellation`; `GET /bookings/{bookingId}/cancellationPreview`; policy `Customer`
- Response: `200` `CancellationPreview` (`bookingId`, `totalAmount`, `currency`, `refundPercent`, `refundAmount`, `departureAt`, `calculatedAt`)
- Problems: `UNAUTHORIZED` 401, `FORBIDDEN` 403, `NOT_FOUND` 404, `BOOKING_NOT_CONFIRMED` 409 (`bookingStatus`), `TRIP_DEPARTED` 409 (`departedAt`)
- Reads: `bookings.bookings`, `trip_snapshots.trip_snapshots` (reader connection; the preview gates no write). Writes: nothing. Emits: nothing.

## Acceptance criteria
Each maps to tests in `PreviewBookingCancellation.Tests.cs`.

- [x] AC-1: 30 hours before departure, 90%, 989 of 1099 — `HandleAsync_ConfirmedBookingDepartingInThirtyHours_ShowsNinetyPercentAndTheAmountRoundedDown`,
  `HandleAsync_ConfirmedBooking_ChangesNothing`, `Preview_ConfirmedBooking_ReturnsThePreviewAsTheContractShapesIt`
- [x] AC-2: tier boundaries (exactly 24 h -> 90, exactly 6 h -> 50, a minute under 6 h -> 0) — `HandleAsync_TimeLeftOnATierBoundary_AppliesTheHigherTier`
- [x] AC-3: not confirmed / departed / not the customer's / unknown / no token — `HandleAsync_BookingThatIsNotConfirmed_IsRefusedNamingItsStatus`,
  `HandleAsync_TripAlreadyDeparted_IsRefusedWithTheDepartureTime`, `HandleAsync_AnotherCustomersBooking_IsRefusedAsNotTheirs`,
  `HandleAsync_UnknownBooking_IsRefusedNotFound`, `Preview_WithoutAnAccessToken_IsRefusedUnauthorized`,
  `Preview_CallerThatIsNotACustomer_IsRefusedForbidden`, `Preview_AnotherCustomersBooking_IsRefusedForbiddenWithItsCode`,
  `Preview_UnknownBooking_IsRefusedNotFoundWithItsCode`, `Preview_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus`,
  `Preview_DepartedTrip_IsRefusedWithItsCodeAndDepartureTime`

## Invariants touched
- The share is `RefundRules.CalculatePercent(departureAt - now, settings.CancellationTiers)`, the amount `RefundRules.CalculateAmount`; the
  settings are read once per request (`IBookingSettings.Current`).
- Check order: not found -> not the caller's (403) -> not Confirmed -> departed (`BookingRules.HasDeparted`; exactly at departure is not departed).
- Admin and operator tokens are refused by the `Customer` policy (403) before the handler runs.

## Known deviation
- Another customer's booking answers `403 FORBIDDEN`, not 404: the contract requires it and overrides the generic out-of-scope rule.
