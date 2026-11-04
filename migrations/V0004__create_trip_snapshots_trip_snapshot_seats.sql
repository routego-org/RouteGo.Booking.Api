-- =============================================================================
-- V0004__create_trip_snapshots_trip_snapshot_seats.sql
-- One seat of a trip as Booking knows it: its label, its class and its current fare.
-- =============================================================================
-- Implements: data-model/trip-snapshots/trip-snapshot-seat.md v0.1.0
-- Change tracking: excluded - see the audit.fn_exclude_from_audit reason below.
-- Identifier: keyed by the entity's identity, the pair (trip_id, seat_no).
-- Money: whole Indian rupees, held directly in bigint (there are no minor units).

BEGIN;

CREATE TABLE trip_snapshots.trip_snapshot_seats (
    trip_id    uuid   NOT NULL,
    seat_no    text   NOT NULL,
    seat_class text   NOT NULL,
    fare       bigint NOT NULL,
    CONSTRAINT pk_trip_snapshot_seats PRIMARY KEY (trip_id, seat_no),
    -- Relationship: "Belongs to one TripSnapshot".
    CONSTRAINT fk_trip_snapshot_seats_trip_id FOREIGN KEY (trip_id) REFERENCES trip_snapshots.trip_snapshots (trip_id),
    CONSTRAINT ck_trip_snapshot_seats_seat_no_len CHECK (length(seat_no) BETWEEN 1 AND 16),
    CONSTRAINT ck_trip_snapshot_seats_seat_class_len CHECK (length(seat_class) BETWEEN 1 AND 64),
    -- Attribute fare: whole rupees, never negative.
    CONSTRAINT ck_trip_snapshot_seats_fare CHECK (fare >= 0)
);

SELECT audit.fn_exclude_from_audit(
    'trip_snapshots.trip_snapshot_seats',
    'machine-written projection of Fleet''s TripScheduled and FareChanged events, rebuilt from them; a trip''s own change history is Fleet''s');

COMMENT ON TABLE trip_snapshots.trip_snapshot_seats IS 'data-model/trip-snapshots/trip-snapshot-seat.md';

COMMIT;
