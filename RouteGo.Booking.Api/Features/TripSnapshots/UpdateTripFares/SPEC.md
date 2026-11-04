# CAP-SNAP-002 — Update trip fares

- Capability: CAP-SNAP-002
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/trip-snapshots/features/trip-snapshots.md` (v0.1.0)
- Slice: `Features/TripSnapshots/UpdateTripFares/`

## Contract

`kind: worker`, event `FareChanged` on `booking-fleet-q`. Handler `UpdateTripFaresHandler : IEventWorker`.
Data: `tripId`, `seats[{seatNo, class, fare}]` (class fares and seat class ignored).
Writes `trip_snapshots.trip_snapshots.fares_changed_at`, `trip_snapshots.trip_snapshot_seats.fare`. Emits no booking event.
Alerts (by the queue): `FLEET_EVENT_NOT_APPLIED` with reason `invalidEventData`, `tripUnknown`, `repeatedFailure`.

## Acceptance criteria

- [x] AC-6: new fares for new bookings, booked fares kept — `HandleAsync_FareChange_GivesNewBookingsTheNewFaresAndKeepsBookedFares`
- [x] AC-7: only a later change applies — `HandleAsync_EarlierFareChangeArrivingLate_KeepsTheLaterFaresAndRepeatChangesNothing`
- [x] AC-8: unknown trip is postponed (`tripUnknown`) then applied — `HandleAsync_FareChangeForAnUnknownTrip_IsPostponedAsTripUnknownThenAppliedOnceTheTripExists`, `HandleAsync_FareChangeForATripKnownOnlyAsCancelled_IsPostponedAsTripUnknown`
- [x] Unusable data is rejected — `HandleAsync_DataMissingAMember_IsRejectedAsInvalidAndChangesNothing`

## Invariants touched

- Newest wins by the envelope `occurredAt` against `fares_changed_at` (equal counts as already applied); decided under the snapshot row's lock.
- A snapshot without details (remembered only from an early cancellation) counts as an unknown trip.
- Booking seats keep their own fares; they are never touched.
