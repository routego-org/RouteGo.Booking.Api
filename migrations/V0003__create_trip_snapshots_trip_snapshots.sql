-- =============================================================================
-- V0003__create_trip_snapshots_trip_snapshots.sql
-- Booking's own copy of one dated trip Fleet announced.
-- =============================================================================
-- Implements: data-model/trip-snapshots/trip-snapshot.md v0.1.0
-- Change tracking: excluded - see the audit.fn_exclude_from_audit reason below.
-- Identifier: keyed by the entity's identity attribute trip_id (uuid), the same value Fleet uses.
-- operator_id is Fleet's operator identifier and the operatorId claim of an operator's token: text,
--   no foreign key (it names a record in another service).
-- The detail columns are nullable on purpose: a TripCancelled can arrive before the trip's
--   TripScheduled, and is remembered as a cancelled snapshot holding only the trip identifier.

BEGIN;

CREATE SCHEMA IF NOT EXISTS trip_snapshots;

CREATE TABLE trip_snapshots.trip_snapshots (
    trip_id               uuid        NOT NULL,
    operator_id           text        NULL,
    operator_name         text        NULL,
    operator_email        text        NULL,
    source_city_name      text        NULL,
    destination_city_name text        NULL,
    service_date          date        NULL,
    departure_at          timestamptz NULL,
    arrival_at            timestamptz NULL,
    bus_name              text        NULL,
    bus_type              text        NULL,
    is_ac                 boolean     NULL,
    is_cancelled          boolean     NOT NULL DEFAULT false,
    cancellation_reason   text        NULL,
    cancelled_at          timestamptz NULL,
    fares_changed_at      timestamptz NULL,
    CONSTRAINT pk_trip_snapshots PRIMARY KEY (trip_id),
    CONSTRAINT ck_trip_snapshots_operator_id_len CHECK (operator_id IS NULL OR length(operator_id) <= 64),
    CONSTRAINT ck_trip_snapshots_operator_name_len CHECK (operator_name IS NULL OR length(operator_name) <= 200),
    CONSTRAINT ck_trip_snapshots_operator_email_len CHECK (operator_email IS NULL OR length(operator_email) <= 254),
    CONSTRAINT ck_trip_snapshots_source_city_name_len CHECK (source_city_name IS NULL OR length(source_city_name) <= 100),
    CONSTRAINT ck_trip_snapshots_destination_city_name_len CHECK (destination_city_name IS NULL OR length(destination_city_name) <= 100),
    CONSTRAINT ck_trip_snapshots_bus_name_len CHECK (bus_name IS NULL OR length(bus_name) <= 200),
    -- Attribute bus_type: exactly the values the entity file lists.
    CONSTRAINT ck_trip_snapshots_bus_type CHECK (bus_type IS NULL OR bus_type IN ('Seater', 'Sleeper', 'SemiSleeper')),
    CONSTRAINT ck_trip_snapshots_cancellation_reason_len CHECK (cancellation_reason IS NULL OR length(cancellation_reason) <= 500),
    -- Attribute note: the details are all present once TripScheduled is applied, all absent before.
    CONSTRAINT ck_trip_snapshots_details_together CHECK (
        (operator_id IS NULL) = (operator_name IS NULL)
        AND (operator_id IS NULL) = (operator_email IS NULL)
        AND (operator_id IS NULL) = (source_city_name IS NULL)
        AND (operator_id IS NULL) = (destination_city_name IS NULL)
        AND (operator_id IS NULL) = (service_date IS NULL)
        AND (operator_id IS NULL) = (departure_at IS NULL)
        AND (operator_id IS NULL) = (arrival_at IS NULL)
        AND (operator_id IS NULL) = (bus_name IS NULL)
        AND (operator_id IS NULL) = (bus_type IS NULL)
        AND (operator_id IS NULL) = (is_ac IS NULL)),
    -- Rule: a cancelled snapshot carries the time Booking learned of it.
    CONSTRAINT ck_trip_snapshots_cancelled_at CHECK (is_cancelled = (cancelled_at IS NOT NULL))
);

SELECT audit.fn_exclude_from_audit(
    'trip_snapshots.trip_snapshots',
    'machine-written projection of Fleet''s TripScheduled, FareChanged and TripCancelled events, rebuilt from them; Fleet holds the change history of the trip itself');

COMMENT ON TABLE trip_snapshots.trip_snapshots IS 'data-model/trip-snapshots/trip-snapshot.md';

COMMIT;
