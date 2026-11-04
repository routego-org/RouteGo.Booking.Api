# CAP-BKG-005 — Change a booking's seats

- Capability: CAP-BKG-005
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/booking-seat-change.md` (v0.1.1)
- Area: Bookings
- Slice: `Features/Bookings/CreateSeatChange/`

## Contract
Implemented verbatim from `contracts/bookings/seat-change.yaml`.

- Operation: `createSeatChange`; `POST /bookings/{bookingId}/seatChanges`; policy `Customer`
- Request: `changes` [`fromSeatNo`, `toSeatNo`], 1..`BookingRules.MaxSeatsCeiling` (6) moves, no seat named twice as a from-seat or twice as a to-seat, no move to itself
  (a swap such as 4C->4D with 4D->4C is valid)
- Response: `201` `SeatChange` (`bookingId`, `changes`, `seatChangeCount`, `seats`) with `Location: /bookings/{bookingId}`; `200` for a repeat (empty `changes`)
- Problems: `VALIDATION_FAILED` 400, `UNAUTHORIZED` 401, `FORBIDDEN` 403, `NOT_FOUND` 404, `BOOKING_NOT_CONFIRMED` 409, `TRIP_CANCELLED` 409, `BOOKING_CLOSED` 409,
  `SEATS_UNAVAILABLE` 409, `SEAT_CHANGE_LIMIT_REACHED` 422, `SEATS_NOT_IN_BOOKING` 422, `UNKNOWN_SEATS` 422, `SEAT_CLASS_MISMATCH` 422, `UPSTREAM_UNAVAILABLE` 502,
  `UPSTREAM_TIMEOUT` 504 (`retryAfterSeconds` 5), and `CONFLICT` 409 if the booking changed while Seat Inventory worked.
- Calls: `ISeatInventoryClient.ChangeSeatsAsync`, outside any transaction.
- Writes (one transaction, audit action `CreateSeatChange`, only after Seat Inventory answered `Changed`): `bookings.booking_seats` (seat label moves, passenger/fare/class stay),
  `bookings.bookings.seat_change_count + 1` (conditional on status Confirmed and the count that was read), `bookings.booking_events`.
- Emits: `BookingSeatsChanged` (full ticket after the change, the moves, the new count).

## Acceptance criteria
Each maps to tests in `CreateSeatChange.Tests.cs` (handler) and `CreateSeatChange.Http.Tests.cs` (host and validator).

- [x] AC-1: move one seat — `HandleAsync_MovingOneSeat_MovesThePassengerAndFareCountsOneChangeAndKeepsTheTotal`,
  `HandleAsync_MovingOneSeat_RecordsBookingSeatsChangedWithTheTicketAfterTheChangeAndTheMove`, `HandleAsync_SwappingTwoSeats_SwapsThePassengersAndKeepsEachFareAndClass`,
  `SeatChange_FreeSeat_ReturnsCreatedWithTheSeatsAfterTheChangeAndTheBookingsLocation`
- [x] AC-2: all or nothing — `HandleAsync_SeatInventoryNamesAnUnavailableSeat_IsRefusedNamingItAndNothingChanges`, `SeatChange_NewSeatTaken_IsRefusedSeatsUnavailableNamingTheSeat`
- [x] AC-3: same class only — `HandleAsync_MoveToASeatOfAnotherClass_IsRefusedNamingTheMoveWithoutAskingSeatInventory`, `SeatChange_SeatOfAnotherClass_IsRefusedSeatClassMismatchNamingTheMove`
- [x] AC-4: only the booking's seats — `HandleAsync_MoveFromASeatThatIsNotTheBookings_IsRefusedNamingIt`, `HandleAsync_MoveToASeatTheTripDoesNotHave_IsRefusedNamingIt`
- [x] AC-5: cutoff — `HandleAsync_ThirtyMinutesBeforeDeparture_IsStillAccepted`, `HandleAsync_TwentyNineMinutesBeforeDeparture_IsRefusedWithTheDepartureAndTheCutoff`,
  `SeatChange_ChangesClosed_IsRefusedWithItsCodeDepartureAndCutoff`
- [x] AC-6: at most 3 changes — `HandleAsync_AfterTheThirdSeatChange_IsRefusedWithTheLimitAndNothingChanges`, `HandleAsync_LimitReachedAndASeatNotTheBookings_RefusesTheLimitFirst`,
  `SeatChange_LimitReached_IsRefusedWithItsCodeAndTheLimit`
- [x] AC-7: repeat — `HandleAsync_TheSameChangeAgain_SucceedsWithNoMovesNoCountAndNoEvent`, `HandleAsync_TheSameChangeAgainAtTheLimit_StillSucceeds`,
  `SeatChange_RepeatThroughTheHost_ReturnsOkWithNoChanges`
- [x] AC-8: only a confirmed booking — `HandleAsync_BookingThatIsNotConfirmed_IsRefusedNamingItsStatus`, `SeatChange_PaymentPendingBooking_IsRefusedWithItsCodeAndStatus`
- [x] AC-9: not on a cancelled trip — `HandleAsync_TripCancelledInTheSnapshot_IsRefusedTripCancelled`
- [x] AC-10: request rules — `Validate_*` tests, `SeatChange_NoMoves_IsRefusedValidationFailedNamingTheField`
- [x] AC-11: only the booking's customer — `HandleAsync_AnotherCustomersBooking_IsRefusedAsNotTheirs`, `HandleAsync_UnknownBooking_IsRefusedNotFound`,
  `SeatChange_WithoutAnAccessToken_IsRefusedUnauthorized`, `SeatChange_CallerThatIsNotACustomer_IsRefusedForbidden`, `SeatChange_AnotherCustomersBooking_IsRefusedForbiddenWithItsCode`,
  `SeatChange_UnknownBooking_IsRefusedNotFoundWithItsCode`
- [x] AC-12: Seat Inventory down or slow — `HandleAsync_SeatInventoryDoesNotMoveTheSeats_BookingIsUntouchedAndTheRefusalIsMapped` (every non-`Changed` answer),
  `HandleAsync_SeatInventoryDown_IsRetryableAndTimeoutIsNamedApart`, `SeatChange_SeatInventoryDown_IsRefusedBadGatewayWithItsCode`, `SeatChange_SeatInventorySlow_IsRefusedGatewayTimeoutWithItsCode`

## Invariants touched
- Check order: not found -> not the caller's (403) -> Confirmed -> trip not cancelled in the snapshot -> cutoff (`BookingRules.IsBookingClosed`) -> REPEAT (every to-seat is the
  booking's and no from-seat is) -> limit (`SeatChangeLimit`) -> from-seats are the booking's -> to-seats are seats of the snapshot -> same class per move. Nothing changes on any refusal.
- The gating read runs in a write transaction that is never committed; the Seat Inventory call is outside any transaction; the recording transaction re-checks status and count in the same
  conditional UPDATE. If it matches no row (booking changed meanwhile) the answer is `409 CONFLICT`; retrying is safe because Seat Inventory treats a done move as done.
- Seat rows move in two UPDATEs of one command (place-holder label `~<position>`, then the new label), so a swap never meets the (booking, seat) key; passenger, fare and class never leave their row.
- Settings (`SeatChangeLimit`, `CutoffMinutes`) are read once per request.
- Seat Inventory `Refused` (unknown or departed trip) is answered `502 UPSTREAM_UNAVAILABLE` with `retryable: false`.

## Known deviation
- The spec's "Proposal" (Booking repeats a timed-out change in the background until Seat Inventory answers) is deliberately NOT built. On a timeout or outage the booking is untouched and the customer
  retries; a retried identical request is repeat-safe on both sides.
- No `ILogger` in the handler: its four ports already fill the constructor budget.
