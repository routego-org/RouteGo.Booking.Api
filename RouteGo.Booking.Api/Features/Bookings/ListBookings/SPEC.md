# CAP-BKG-003 — List bookings

- Capability: CAP-BKG-003
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/my-bookings.md` (v0.1.0)
- Area: Bookings
- Slice: `Features/Bookings/ListBookings/`

## Contract

- Operation: `listBookings` — `contracts/bookings/booking.yaml`
- Route / verb: `GET /bookings`, policy `Customer`
- Request: query `page` (1-based, default 1), `pageSize` (1 to 200, default 20)
- Response: `200` `BookingPage` (`items` of `BookingSummary`, `page`, `pageSize`, `totalItems`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403)
- Reads: `bookings.bookings`, `bookings.booking_seats` (aggregated per booking), `trip_snapshots.trip_snapshots`, `cancellations.refunds`
  (reader connection; one count query and one page query, no N+1; no writes)
- Emits: nothing

## Acceptance criteria

Each criterion maps to tests in `ListBookings.Tests.cs` (handler, real Postgres) and `ListBookings.Http.Tests.cs` (through the host).

- [x] AC-1: newest first, with refunds — `HandleAsync_ConfirmedCancelledAndExpiredBookings_ListsNewestFirstWithRefundsTripSeatsAndTotal`, `ListBookings_CustomerWithAConfirmedBooking_Returns200WithThePageShape`
- [x] AC-2: bookings still being paid are not listed — `HandleAsync_BookingStillBeingPaid_IsNotListedUntilItIsConfirmed`, `HandleAsync_InitiatedOrSeatsHeldBooking_IsNotListed`
- [x] AC-3: only the customer's own — `HandleAsync_AnotherCustomerHasBookings_ListsNoneOfThem`
- [x] AC-4: paging, page size 0 or 201 refused — `HandleAsync_FortyFiveBookingsPageThreeOfTwenty_ReturnsTheFiveOldestAndTheTotal`, `Validate_PageOrPageSizeOutOfRange_IsRefused`, `Validate_PageAndPageSizeInRange_IsAccepted`, `ListBookings_PageOrPageSizeOutOfRange_IsRefusedValidationFailed`
- [x] AC-5: no bookings yet — `HandleAsync_CustomerWhoNeverBooked_ReturnsAnEmptyPageNotAnError`
- [x] AC-6: customers only — `ListBookings_NotACustomer_IsRefusedForbidden`, `ListBookings_WithoutAccessToken_IsRefusedUnauthorized`
- [x] Proposal (open question 1): failed-while-being-created bookings left out — `HandleAsync_BookingThatFailedWhileBeingCreated_IsNotListed`, `HandleAsync_BookingThatFailedAfterReachingPayment_IsListed`

## Invariants touched

- Ownership is the query's `WHERE customer_id = @CustomerId` (the token's subject).
- Listed statuses: Confirmed, Cancelled, Failed, Expired; order `created_at DESC, booking_id`.

## Notes

- OPEN QUESTION for the Booking product owner (feature spec, "Proposal" and open question 1): this slice implements the
  proposal, so a Failed booking whose reason is SeatsUnavailable, InventoryUnavailable or PaymentUnavailable is NOT listed.
  If the owner answers "list every Failed booking", delete the failure-reason predicate from both statements in the handler
  and the `...FailedWhileBeingCreated_IsNotListed` test.
- Open question 2 (showing the customer's one unpaid booking) is not implemented; the contract and rule 3 exclude unpaid bookings.
- Paging bounds (1..200, default 20) are constants of this slice; no shared paging helper exists in `Domain/` or `Common/`.
