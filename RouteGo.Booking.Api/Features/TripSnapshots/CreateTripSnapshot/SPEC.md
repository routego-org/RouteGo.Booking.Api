# CAP-SNAP-001 — Create trip snapshot

- Capability: CAP-SNAP-001
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/trip-snapshots/features/trip-snapshots.md` (v0.1.0)
- Slice: `Features/TripSnapshots/CreateTripSnapshot/`

## Contract

`kind: worker`, event `TripScheduled` on `booking-fleet-q`. Handler `CreateTripSnapshotHandler : IEventWorker`.
Data (Fleet's real wire): `tripId`, `operatorId`, `operatorName`, `operatorEmail`, `busName`, `busType`, `isAc`,
`source`/`destination` objects (city name read as `cityName`, or `name` as Fleet writes it), `serviceDate`, `departureUtc`,
`arrivalUtc`, `seats[{seatNo, class, fare}]`; other members ignored. The `class` member is read from the JSON directly.
Writes `trip_snapshots.trip_snapshots` and `trip_snapshots.trip_snapshot_seats` in one transaction. Emits no booking event.
Alerts: `FLEET_EVENT_NOT_APPLIED` reason `invalidEventData`.

## Acceptance criteria

- [x] AC-1: new trip gets its snapshot — `HandleAsync_NewTrip_CreatesTheSnapshotWithAllItsSeats`
- [x] AC-2: created once — `HandleAsync_TripAlreadyKnown_LeavesTheSnapshotAndItsFaresUnchanged`
- [x] AC-3: early cancellation honoured — `HandleAsync_TripRememberedAsCancelled_FillsInTheDetailsAndStaysCancelled`
- [x] AC-4: unusable data — `HandleAsync_DataMissingAMember_IsRejectedAsInvalidAndCreatesNothing`, `HandleAsync_TwoSeatsWithTheSameLabel_...`, `HandleAsync_SeatWithoutAClassOrFare_...`
- [x] AC-5: a waiting event is processed on start — queue semantics (not a handler test)
- [x] Fleet's city member name — `HandleAsync_CitiesNamedWithTheirNameMember_ReadsTheNames`

## Invariants touched

- Details are written only while `operator_id IS NULL` (insert or fill-in of an early cancellation); `is_cancelled` is never touched.
- Seats are inserted set-based in the same transaction as the snapshot, all or none.
