-- =============================================================================
-- V0009__create_bookings_booking_events.sql
-- An event recorded with the booking change that caused it, kept until it has been sent.
-- =============================================================================
-- Implements: data-model/bookings/booking-event.md v0.1.0
-- Change tracking: excluded - see the audit.fn_exclude_from_audit reason below.
-- Identifier: keyed by the entity's identity attribute event_id (uuid), fixed when the event is
--   recorded and the same on every send.
-- Order: events are sent "in the order recorded". recorded_at defaults to clock_timestamp() (the
--   moment of each INSERT, not of the transaction), so two events recorded by one transaction in
--   order keep that order; the publisher reads by recorded_at.

BEGIN;

CREATE TABLE bookings.booking_events (
    event_id       uuid        NOT NULL,
    event_type     text        NOT NULL,
    stream         text        NOT NULL,
    booking_id     uuid        NOT NULL,
    event_data     text        NOT NULL,
    correlation_id text        NOT NULL,
    recorded_at    timestamptz NOT NULL DEFAULT clock_timestamp(),
    sent_at        timestamptz NULL,
    CONSTRAINT pk_booking_events PRIMARY KEY (event_id),
    -- Relationship: "Belongs to one Booking".
    CONSTRAINT fk_booking_events_booking_id FOREIGN KEY (booking_id) REFERENCES bookings.bookings (booking_id),
    -- Attribute event_type: exactly the values the entity file lists.
    CONSTRAINT ck_booking_events_event_type CHECK (event_type IN
        ('BookingConfirmed', 'BookingSeatsChanged', 'BookingFailed', 'BookingExpired', 'BookingCancelled', 'RefundRequested', 'AuditRecorded')),
    -- Attribute stream: exactly the values the entity file lists.
    CONSTRAINT ck_booking_events_stream CHECK (stream IN ('BookingEvents', 'AuditEvents')),
    -- Attribute stream: "AuditRecorded to the audit events stream, every other event to the booking events stream".
    CONSTRAINT ck_booking_events_stream_matches_type CHECK ((event_type = 'AuditRecorded') = (stream = 'AuditEvents')),
    CONSTRAINT ck_booking_events_event_data_len CHECK (length(event_data) <= 32000),
    CONSTRAINT ck_booking_events_correlation_id_len CHECK (length(correlation_id) <= 128)
);

-- The publisher's one query: the unsent events, oldest first.
CREATE INDEX ix_booking_events_unsent ON bookings.booking_events (recorded_at) WHERE sent_at IS NULL;
CREATE INDEX ix_booking_events_booking_id ON bookings.booking_events (booking_id);

SELECT audit.fn_exclude_from_audit(
    'bookings.booking_events',
    'machine-written outbox: rows are written by the service in the same transaction as the change they announce and only ever gain sent_at; their history is the event stream itself');

COMMENT ON TABLE bookings.booking_events IS 'data-model/bookings/booking-event.md';

COMMIT;
