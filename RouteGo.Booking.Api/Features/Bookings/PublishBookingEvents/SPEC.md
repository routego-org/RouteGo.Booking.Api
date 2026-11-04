# CAP-BKG-011 — Publish booking events

- Capability: CAP-BKG-011
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/bookings/features/booking-event-publishing.md` (v0.1.0)
- Area: Bookings
- Slice: `Features/Bookings/PublishBookingEvents/`

## Contract

`kind: worker` — no HTTP operation. Trigger `schedule: every 5 seconds`, run in-process by
`PublishBookingEventsService` (a `BackgroundService`); one run is `PublishBookingEventsHandler.HandleAsync`.

- Reads: `bookings.booking_events` rows with no `sent_at`, in recorded order (`recorded_at`, then `event_id`), 100 per run.
- Sends to: `IBookingEventPublisher` — the booking events stream for every type but `AuditRecorded`, the audit events stream for
  `AuditRecorded` (the row's `stream` value `BookingEvents` / `AuditEvents`). Message attribute `eventType`; RouteGo envelope
  (`eventId`, `eventType`, `version` 1, `occurredAt` = recorded time, `correlationId`, `producer` `booking-service`, `data`).
- Writes: `sent_at` only, as the system actor with audit action `job:PublishBookingEvents` (the table is excluded from audit).
- Emits: `BOOKING_EVENT_DELAYED` (`ProblemTypes.BookingEventDelayed`) — one Error log line (at most one a minute) naming the oldest
  unsent event's time and the lag in seconds.

## Acceptance criteria

Each criterion maps to tests in `PublishBookingEvents.Tests.cs`, run against a real Postgres.

- [x] AC-1: A recorded event is sent with the envelope — `HandleAsync_RecordedBookingEvent_SendsItWithTheEnvelopeAndMarksItSent`
- [x] AC-2: Audit records go to the audit events stream — `HandleAsync_CancellationEvents_GoToTheBookingStreamAndAuditRecordedToTheAuditStream`
- [x] AC-3: No change without its event, even across a crash — `HandleAsync_EventRecordedBeforeAStop_IsSentByTheRunAfterTheRestart`
- [x] AC-4: Events are sent in the order recorded — `HandleAsync_TwoEventsRecordedInOrder_SendsThemInThatOrder`
- [x] AC-5: A resent event keeps its event ID — `HandleAsync_EventSentButNeverAcknowledged_IsSentAgainWithTheSameEventId`
- [x] AC-6: Booking keeps working while messaging is down — `HandleAsync_MessagingDownThenBack_HoldsEveryEventBackThenSendsAllInOrder`
- [x] AC-7: Alert when more than 5 minutes behind — `HandleAsync_OldestUnsentEventOverFiveMinutesOld_RaisesTheDelayAlert`, `HandleAsync_OldestUnsentEventUnderFiveMinutesOld_RaisesNoAlert`
- Also: one publisher at a time — `HandleAsync_AnotherPublisherHoldsTheLock_DoesNothing`; the loop — `ExecuteAsync_RunningService_SendsARecordedEventWithinTheNextRuns`

## Invariants touched

- One publisher at a time across every running copy: transaction-scoped advisory lock `pg_try_advisory_xact_lock(4711201)`; a copy that
  does not get it does nothing. 4711201 is this publisher's own key.
- A send that fails holds back every later event (nothing is skipped); the event ID is fixed at recording, so a resend is harmless.
- Ordering across different bookings is not promised; each event is stamped at its own insert (`clock_timestamp()`).
