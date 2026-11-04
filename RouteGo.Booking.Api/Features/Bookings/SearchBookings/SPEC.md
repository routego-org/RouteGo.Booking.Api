# CAP-BKG-006 — Search bookings

- Capability: CAP-BKG-006
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/admin-booking-search.md` (v0.1.0)
- Area: Bookings
- Slice: `Features/Bookings/SearchBookings/`

## Contract

- Operation: `searchBookings` — `contracts/bookings/booking.yaml`
- Route / verb: `POST /bookings/search`, policy `Admin` (a query by POST so the email stays out of the address; it writes nothing)
- Request: body `BookingSearch` (`bookingId?`, `contactEmail?`, `tripId?`), query `page` (default 1), `pageSize` (1 to 200, default 20)
- Response: `200` `AdminBookingPage` (`items` of `AdminBookingSummary`, `page`, `pageSize`, `totalItems`)
- Problem codes: `VALIDATION_FAILED` (400), `UNAUTHORIZED` (401), `FORBIDDEN` (403)
- Reads: `bookings.bookings`, `bookings.booking_seats` (aggregated per booking), `trip_snapshots.trip_snapshots` (reader connection; no writes)
- Emits: nothing
- verb-justified: POST is a read here; the contract says so (the email must not appear in an address).

## Acceptance criteria

Each criterion maps to tests in `SearchBookings.Tests.cs` (handler and validator, real Postgres) and `SearchBookings.Http.Tests.cs` (through the host).

- [x] AC-1: search by contact email, any letter case — `HandleAsync_EmailInOtherLetterCase_ListsThatContactsBookingsNewestFirstWithEveryDetail`, `SearchBookings_AdminSearchesByEmail_Returns200WithThePageShape`
- [x] AC-2: search by trip, every status — `HandleAsync_TripWithBookingsInEveryStatus_ListsAllOfThemIncludingTheUnpaidOne`
- [x] AC-3: by booking id, and combined criteria — `HandleAsync_BookingIdGiven_ListsExactlyThatBooking`, `HandleAsync_EmailAndTripGiven_ListsOnlyThatContactsBookingsOnThatTrip`
- [x] AC-4: nothing found — `HandleAsync_EmailNoBookingHas_ReturnsAnEmptyPageNotAnError`
- [x] AC-5: at least one criterion — `Validate_NoBookingIdNoEmailNoTrip_IsRefusedNamingTheSearch`, `Validate_OnlyABlankEmail_IsRefused`, `Validate_AnyOneCriterion_IsAccepted`, `SearchBookings_NoCriterion_IsRefusedValidationFailed`
- [x] AC-6: paging — `HandleAsync_FortyFiveBookingsOnATripPageTwoOfTwenty_ReturnsBookingsTwentyOneToFortyNewestFirst`, `Validate_PageOrPageSizeOutOfRange_IsRefused`, `SearchBookings_PageOrPageSizeOutOfRange_IsRefusedValidationFailed`
- [x] AC-7: admins only — `SearchBookings_NotAnAdmin_IsRefusedForbidden`, `SearchBookings_WithoutAccessToken_IsRefusedUnauthorized`

## Invariants touched

- Every criterion given must match; an absent criterion matches everything. The email is matched whole as `lower(contact_email) = lower(@Email)` (index `ix_bookings_contact_email_lower`).
- Every status is searched; order `created_at DESC, booking_id`.

## Notes

- A blank or whitespace-only `contactEmail` counts as not given (so it alone fails the at-least-one rule); the email is trimmed.
- The at-least-one failure is reported under the field name `search` in the `errors` of the VALIDATION_FAILED body.
