# CAP-BKG-001 — Create a booking

- Capability: CAP-BKG-001
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/booking-creation.md` (v0.1.3)
- Area: Bookings
- Slice: `Features/Bookings/CreateBooking/`

## Contract
Implemented **verbatim** from the issued contract.

- Operation: `createBooking` — `contracts/bookings/booking.yaml`
- Route / verb: `POST /bookings`, role Customer (`Policies.Customer`)
- Request: `BookingRequest` (`tripId`, `contactName`, `seats[]` of `seatNo`/`passengerName`/`passengerAge`/`passengerGender`, `expectedTotal`)
- Response: `201` with `Booking` and `Location: /bookings/{bookingId}`
- Reads: `trip_snapshots.trip_snapshots`, `trip_snapshots.trip_snapshot_seats` (inside the write transaction)
- Writes: `bookings.bookings`, `bookings.booking_seats`, `bookings.booking_events` (BookingFailed only)
- Calls: `ISeatInventoryClient.HoldSeatsAsync` / `ReleaseSeatsAsync`, `IPaymentClient.StartPaymentAsync`, never inside an open transaction
- Emits (events): `BookingFailed` for every booking that ends Failed here, recorded in the same transaction as the move.
  Creating, holding and starting a payment record no event.

## Problem codes by branch
| Branch | ProblemTypes | HTTP |
| --- | --- | --- |
| Request shape | `ValidationFailed` | 400 |
| No/invalid token | `Unauthorized` | 401 |
| Not a customer | `Forbidden` | 403 |
| No snapshot, or snapshot without details (early TripCancelled) | `NotFound` | 404 |
| Snapshot cancelled | `TripCancelled` | 409 |
| Less than the cutoff before departure | `BookingClosed` (`departureAt`, `cutoffMinutes`) | 409 |
| Seat not in snapshot | `UnknownSeats` | 422 |
| Total differs | `PriceChanged` (`currentTotal`, `seatFares`) | 409 |
| Insert conflicts with the customer's unpaid booking | `UnpaidBookingExists` (`bookingId`, `holdExpiresAt`) | 409 |
| SI: SeatsUnavailable | `SeatsUnavailable` (Failed/SeatsUnavailable) | 409 |
| SI: TripCancelled | `TripCancelled` (Failed/TripCancelled) | 409 |
| SI: TimedOut | `UpstreamTimeout` (Failed/InventoryUnavailable, release) | 504 |
| SI: Unavailable | `UpstreamUnavailable` retryable (Failed/InventoryUnavailable, release) | 502 |
| SI: UnknownSeats / Refused | `UpstreamUnavailable` not retryable (Failed/InventoryUnavailable, release) | 502 |
| Payment: Unavailable | `UpstreamUnavailable` retryable (Failed/PaymentUnavailable, release) | 502 |
| Payment: TimedOut | `UpstreamTimeout` (Failed/PaymentUnavailable, release) | 504 |
| A conditional status move or the unpaid-booking read found the booking already moved | `Conflict` (baseline code, not in the spec's `emits`) | 409 |

## Acceptance criteria
Each criterion maps to tests in `CreateBooking.Tests.cs`, `CreateBooking.Failures.Tests.cs`,
`CreateBooking.Concurrency.Tests.cs` and `CreateBooking.Http.Tests.cs`.

- [x] AC-1: Two seats, payment page → `HandleAsync_TwoFreeSeats_CreatesPaymentPendingBookingWithHoldAndCheckout`, `HandleAsync_TwoFreeSeats_AsksSeatInventoryAndPaymentWithTheTokenIdentityAndRecordsNoEvent`, `CreateBooking_ValidRequest_Returns201WithLocationAndTheBooking`
- [x] AC-2: Price moved → `HandleAsync_FareRaisedSinceCustomerLooked_RefusesWithCurrentTotalThenAcceptsTheNewTotal`, `CreateBooking_FareChanged_RefusesPriceChangedWithCurrentTotalAndSeatFares`
- [x] AC-3: Seat taken → `HandleAsync_SeatTakenByAnotherBooking_RefusesNamingTheSeatAndRecordsFailedSeatsUnavailable`, `HandleAsync_AfterASeatsUnavailableFailure_TheCustomerMayBookOtherSeatsAtOnce`
- [x] AC-4: 100 customers, one seat → `HandleAsync_HundredCustomersBookTheSameSeatAtOnce_ExactlyOneReachesPaymentPending` (database level; Seat Inventory's atomic grant is a fake)
- [x] AC-5: One unpaid booking → `HandleAsync_ExistingPaymentPendingBooking_IsRefusedNamingItAndItsHoldExpiry`, `HandleAsync_ExistingBookingAlreadyConfirmed_AllowsANewBooking`
- [x] AC-6: Double click → `HandleAsync_TwoIdenticalRequestsAtOnce_CreatesOneBookingAndRefusesTheOther`
- [x] AC-7: Cutoff → `HandleAsync_TwentyNineMinutesBeforeDeparture_IsRefusedBookingClosedWithTheCutoff`, `HandleAsync_TripExactlyAtTheCutoff_IsStillAccepted`
- [x] AC-8: Cancelled trip → `HandleAsync_TripCancelledAndAlsoClosed_IsRefusedTripCancelledAndHoldsNothing`
- [x] AC-9: SI knows the trip cancelled → `HandleAsync_SeatInventoryKnowsTheTripCancelled_RefusesTripCancelledAndRecordsFailedTripCancelled`
- [x] AC-10: Unknown trip and seats → `HandleAsync_TripWithNoSnapshot_IsRefusedNotFound`, `HandleAsync_TripKnownOnlyFromAnEarlyCancellation_IsRefusedNotFound`, `HandleAsync_SeatTheTripDoesNotHave_IsRefusedNamingTheUnknownSeatBeforeThePriceCheck`, `CreateBooking_SeatNotOnTheTrip_Returns422NamingTheSeat`
- [x] AC-11: Request rules → `CreateBooking_BadSeats_IsRefusedValidationFailedNamingTheField`, `CreateBooking_BadContact_IsRefusedValidationFailedNamingTheField`, `CreateBooking_NoExpectedTotal_IsRefusedValidationFailedNamingTheField`, `CreateBooking_MoreSeatsThanTheSettingAllows_IsRefusedValidationFailed`
- [x] AC-12: Only customers → `CreateBooking_WithoutAccessToken_IsRefusedUnauthorized`, `CreateBooking_CallerIsNotACustomer_IsRefusedForbiddenAndHoldsNothing`
- [x] AC-13: SI unreachable → `HandleAsync_SeatInventoryTimesOut_RefusesWithTimeoutReleasesTheHoldAndRecordsFailedInventoryUnavailable`, `HandleAsync_SeatInventoryDown_RefusesUnavailableRetryableAndReleasesTheHold`, `HandleAsync_SeatInventoryRefusesForItsOwnReasons_FailsInventoryUnavailableNotRetryableAndReleases`, `CreateBooking_SeatInventoryTimesOut_Returns504WithRetryAfter`
- [x] AC-14: Payment unreachable → `HandleAsync_PaymentUnavailable_ReleasesTheHoldRecordsFailedPaymentUnavailableAndRefusesUnavailable`, `HandleAsync_PaymentTimesOut_RefusesWithTimeoutAndReleasesTheHold`
- [x] AC-15: Hold length setting → `HandleAsync_HoldLengthSetTo60Seconds_AsksForSixtySecondsAndStoresSeatInventoryExpiry`
- [x] AC-16: BookingFailed announced → `HandleAsync_FailedBooking_RecordsBookingFailedWithBookingTripSeatsContactAndReason`
- Not in the spec's AC list: a booking moved by someone else mid-flight → `HandleAsync_BookingMovedWhileTheHoldWasInFlight_IsReportedStateChangedAndRecordsNoEvent`

## Invariants touched
- Check order: snapshot with details (404) → not cancelled → cutoff → every seat known → total equals expected → insert (unpaid-booking
  conflict). A snapshot remembered only from an early TripCancelled has no details and answers 404, not TRIP_CANCELLED.
- One transaction records the booking `Initiated` and its seats (`INSERT ... ON CONFLICT (customer_id) WHERE status IN (...) DO NOTHING`);
  zero rows means the customer has an unpaid booking. Seat class and fare come from the rows the price check summed.
- Seat Inventory and Payment are called with no transaction open. Every status move is `WHERE status = @From` and checks rows affected.
- A failure is recorded first (move + BookingFailed in one transaction), the best-effort release asked for after the commit.
- The contact email is the token's email; the booking belongs to the token's subject. Fares come from the snapshot, never the caller.
- The settings are read once per request by the controller and handed to the validator and the handler.
- Seat labels and names are trimmed; labels must be distinct after trimming. Age is read as a decimal so 4.5 is named, not a body error.
