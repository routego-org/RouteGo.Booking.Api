-- =============================================================================
-- V0006__create_bookings_booking_seats.sql
-- One seat of a booking: which seat, its class, the fare it was sold at, and its passenger.
-- =============================================================================
-- Implements: data-model/bookings/booking-seat.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: keyed by the entity's identity, the pair (booking_id, seat_no); the audit key column
--   named below is booking_id.
-- Money: whole Indian rupees held directly in bigint.

BEGIN;

CREATE TABLE bookings.booking_seats (
    booking_id       uuid   NOT NULL,
    seat_no          text   NOT NULL,
    seat_class       text   NOT NULL,
    fare             bigint NOT NULL,
    passenger_name   text   NOT NULL,
    passenger_age    integer NOT NULL,
    passenger_gender text   NOT NULL,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_booking_seats PRIMARY KEY (booking_id, seat_no),
    -- Relationship: "Belongs to one Booking".
    CONSTRAINT fk_booking_seats_booking_id FOREIGN KEY (booking_id) REFERENCES bookings.bookings (booking_id),
    CONSTRAINT ck_booking_seats_seat_no_len CHECK (length(seat_no) BETWEEN 1 AND 16),
    CONSTRAINT ck_booking_seats_seat_class_len CHECK (length(seat_class) BETWEEN 1 AND 64),
    -- Attribute fare: whole rupees, never negative.
    CONSTRAINT ck_booking_seats_fare CHECK (fare >= 0),
    -- Attribute passenger_name: 2 to 60 characters.
    CONSTRAINT ck_booking_seats_passenger_name_len CHECK (length(passenger_name) BETWEEN 2 AND 60),
    -- Attribute passenger_age: whole years, 0 to 120.
    CONSTRAINT ck_booking_seats_passenger_age CHECK (passenger_age BETWEEN 0 AND 120),
    -- Attribute passenger_gender: exactly the values the entity file lists.
    CONSTRAINT ck_booking_seats_passenger_gender CHECK (passenger_gender IN ('Male', 'Female', 'Other')),
    CONSTRAINT ck_booking_seats_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_booking_seats_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

SELECT audit.fn_enable_audit('bookings.booking_seats', 'booking_id');

COMMENT ON TABLE bookings.booking_seats IS 'data-model/bookings/booking-seat.md';

COMMIT;
