# CAP-BKG-007 — List trip passengers

- Capability: CAP-BKG-007
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/trip-passenger-list.md` (v0.1.0)
- Area: Bookings
- Slice: `Features/Bookings/ListTripPassengers/`

## Contract

- Operation: `listTripPassengers` — `contracts/bookings/passenger.yaml`
- Route / verb: `GET /trips/{tripId}/passengers`, policy `Operator`
- Response: `200` `TripPassengerList` (`trip`, `seatsBooked`, `passengers`)
- Problem codes: `UNAUTHORIZED` (401), `FORBIDDEN` (403), `NOT_FOUND` (404)
- Reads: `trip_snapshots.trip_snapshots`, `bookings.booking_seats`, `bookings.bookings` (reader connection; no writes)
- Emits: nothing

## Acceptance criteria

Each criterion maps to tests in `ListTripPassengers.Tests.cs` (handler, real Postgres) and `ListTripPassengers.Http.Tests.cs` (through the host).

- [x] AC-1: passenger list in seat order, no contact — `HandleAsync_TwoConfirmedBookings_ListsEverySeatInSeatOrderWithTheTripAndSeatsBooked`, `ListTripPassengers_OwnOperator_Returns200WithPassengersAndNeverTheContact`
- [x] AC-2: only confirmed bookings — `HandleAsync_UnpaidCancelledExpiredAndFailedBookings_AreNotListed`
- [x] AC-3: seat changes are reflected — `HandleAsync_BookingMovedToAnotherSeat_ListsThePassengerOnTheNewSeatOnly`
- [x] AC-4: trip with no confirmed booking — `HandleAsync_TripWithNoConfirmedBooking_ReturnsAnEmptyListWithZeroSeatsBooked`
- [x] AC-5: only the trip's own operator — `HandleAsync_TripOfAnotherOperator_IsForbidden`, `ListTripPassengers_AnotherOperator_IsRefusedForbidden`, `ListTripPassengers_NotAnOperator_IsRefusedForbidden`, `ListTripPassengers_WithoutAccessToken_IsRefusedUnauthorized`
- [x] AC-6: unknown trip — `HandleAsync_TripWithNoSnapshot_IsNotFound`, `HandleAsync_SnapshotWithoutTripDetails_IsNotFound`, `ListTripPassengers_UnknownTrip_IsRefusedNotFound`
- [x] Business rule 4: a cancelled trip stays readable — `HandleAsync_CancelledTrip_IsStillReadableAndItsCancelledBookingsAreNotListed`

## Invariants touched

- The token's `operatorId` (`Caller.OperatorId`) must equal the snapshot's `operator_id`.
- A snapshot without trip details (no `operator_id`) is an unknown trip.
- Passengers are the seats of Confirmed bookings only, ordered by seat label then booking id; the response type has no contact fields.

## Notes

- Spec-mandated 403: another operator's trip is FORBIDDEN, not NOT_FOUND (feature spec AC-5), which overrides the coding
  standard's generic "out-of-scope is 404" rule.
- AC-3 holds because a seat change rewrites `booking_seats` in one transaction and this read takes the committed rows; the
  read uses the reader port, which is eventually consistent by its contract (no replica exists today).
- Seat label ordering is the database's text ordering of `seat_no`.
