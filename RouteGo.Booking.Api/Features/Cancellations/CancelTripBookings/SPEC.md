# CAP-CXL-004 — Cancel trip bookings

- Capability: CAP-CXL-004
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/cancellations/features/trip-cancellation-refunds.md` (v0.1.3)
- Slice: `Features/Cancellations/CancelTripBookings/`

## Contract

`kind: worker`, event `TripCancelled` on `booking-fleet-q`. Handler `CancelTripBookingsHandler : IEventWorker`. Data: `tripId`, `reason`.
Writes `trip_snapshots.trip_snapshots`, `bookings.bookings`, `cancellations.cancellations`, `cancellations.refunds`, `bookings.booking_events`.
Emits: `BookingCancelled` + `RefundRequested` (Confirmed), `BookingFailed` (SeatsHeld/PaymentPending). No `AuditRecorded`.
Seat Inventory is not called; it frees seats on receiving the events.
Alerts: `FLEET_EVENT_NOT_APPLIED` reason `invalidEventData` (and `repeatedFailure` by the queue).

## Acceptance criteria

- [x] AC-1: confirmed bookings cancelled, full refund — `HandleAsync_ConfirmedBookings_AreCancelledWithAFullRefundAndAnnounced`
- [x] AC-2: unpaid bookings fail — `HandleAsync_UnpaidBookings_AreFailedAsTripCancelledAndAnnouncedWithContactAndRoute`
- [x] AC-3: late payment refund — belongs to the payment-results slice
- [x] AC-4: finished bookings left alone — `HandleAsync_FinishedAndInitiatedBookings_AreLeftAloneWithNoEvent`
- [x] AC-5: no booking on the trip — snapshot cancelled: `HandleAsync_KnownTrip_IsMarkedCancelledWithFleetsReasonAndKeepsItsDetails`
- [x] AC-6: remembered before the trip is known — `HandleAsync_TripNotYetKnown_IsRememberedAsACancelledSnapshotHoldingOnlyTheTripId`
- [x] AC-7: repeat safety — `HandleAsync_SameCancellationTwiceOrAnotherOne_ChangesNothingAndAnnouncesNothingAgain`
- [x] AC-8: schedule change — same path (reason carried), `HandleAsync_KnownTrip_...`
- [x] AC-9: unusable data — `HandleAsync_DataMissingAMember_IsRejectedAsInvalidAndChangesNothing`
- [x] AC-10: resumes where it stopped — `HandleAsync_FailureAfterSomeBookings_RetryEndsTheRestAndNeverRepeatsOne`

## Invariants touched

- Snapshot is marked cancelled first, in its own transaction; first reason and time stay.
- Each booking is its own transaction: row locked, status re-read, conditional move, rows affected checked.
- Bookings that become confirmed/unpaid after the list was read are caught by a redelivery only (single pass).
