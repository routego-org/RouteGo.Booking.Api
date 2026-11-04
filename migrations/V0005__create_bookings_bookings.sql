-- =============================================================================
-- V0005__create_bookings_bookings.sql
-- One customer's purchase of 1 to 6 seats on one trip, paid with one payment.
-- =============================================================================
-- Implements: data-model/bookings/booking.md v0.1.3
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: keyed by the entity's identity attribute booking_id (uuid), assigned by Booking; Seat
--   Inventory and Payment know the booking by the same value.
-- Money: whole Indian rupees held directly in bigint; the currency is always INR, so it is not stored.
-- customer_id is the access token's subject, an opaque string from Identity: text, no foreign key.
-- created_at is an entity attribute AND an audit column: one column, stamped by the trigger.
-- seat_change_count: the limit (3) is a setting that can change without a release, so the table
--   only forbids a negative count.

BEGIN;

CREATE SCHEMA IF NOT EXISTS bookings;

CREATE TABLE bookings.bookings (
    booking_id             uuid        NOT NULL,
    customer_id            text        NOT NULL,
    contact_name           text        NOT NULL,
    contact_email          text        NOT NULL,
    trip_id                uuid        NOT NULL,
    status                 text        NOT NULL,
    total_amount           bigint      NOT NULL,
    hold_expires_at        timestamptz NULL,
    payment_id             uuid        NULL,
    checkout_url           text        NULL,
    failure_reason         text        NULL,
    payment_failure_detail text        NULL,
    seat_change_count      integer     NOT NULL DEFAULT 0,
    confirmed_at           timestamptz NULL,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_bookings PRIMARY KEY (booking_id),
    -- Relationship: "Belongs to one TripSnapshot, by its trip".
    CONSTRAINT fk_bookings_trip_id FOREIGN KEY (trip_id) REFERENCES trip_snapshots.trip_snapshots (trip_id),
    CONSTRAINT ck_bookings_customer_id_len CHECK (length(customer_id) BETWEEN 1 AND 64),
    -- Attribute contact_name: 2 to 60 characters.
    CONSTRAINT ck_bookings_contact_name_len CHECK (length(contact_name) BETWEEN 2 AND 60),
    CONSTRAINT ck_bookings_contact_email_len CHECK (length(contact_email) BETWEEN 3 AND 254),
    -- Attribute status: exactly the values the entity file lists.
    CONSTRAINT ck_bookings_status CHECK (status IN ('Initiated', 'SeatsHeld', 'PaymentPending', 'Confirmed', 'Failed', 'Expired', 'Cancelled')),
    -- Attribute total_amount: whole rupees, never negative.
    CONSTRAINT ck_bookings_total_amount CHECK (total_amount >= 0),
    CONSTRAINT ck_bookings_checkout_url_len CHECK (checkout_url IS NULL OR length(checkout_url) <= 2000),
    -- Attribute failure_reason: exactly the values the entity file lists, present exactly when Failed.
    CONSTRAINT ck_bookings_failure_reason CHECK (failure_reason IS NULL OR failure_reason IN
        ('SeatsUnavailable', 'InventoryUnavailable', 'PaymentUnavailable', 'PaymentDeclined', 'Abandoned', 'TripCancelled', 'SeatsLost')),
    CONSTRAINT ck_bookings_failure_reason_failed CHECK ((status = 'Failed') = (failure_reason IS NOT NULL)),
    CONSTRAINT ck_bookings_payment_failure_detail_len CHECK (payment_failure_detail IS NULL OR length(payment_failure_detail) <= 500),
    CONSTRAINT ck_bookings_seat_change_count CHECK (seat_change_count >= 0),
    -- Attribute confirmed_at: set once the booking has been Confirmed (it may later be Cancelled).
    CONSTRAINT ck_bookings_confirmed_at CHECK (status NOT IN ('Confirmed', 'Cancelled') OR confirmed_at IS NOT NULL),
    CONSTRAINT ck_bookings_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_bookings_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

-- Rule: "A customer has at most one booking in Initiated, SeatsHeld or PaymentPending at a time."
-- The database enforces it, so two simultaneous requests from one customer cannot both pass.
CREATE UNIQUE INDEX uq_bookings_customer_id_unpaid ON bookings.bookings (customer_id)
    WHERE status IN ('Initiated', 'SeatsHeld', 'PaymentPending');

-- My bookings: one customer's bookings, newest first.
CREATE INDEX ix_bookings_customer_id_created_at ON bookings.bookings (customer_id, created_at DESC);
-- A trip's bookings: passenger list, trip cancellation, admin search by trip.
CREATE INDEX ix_bookings_trip_id_status ON bookings.bookings (trip_id, status);
-- Admin search by contact email, whole and ignoring letter case.
CREATE INDEX ix_bookings_contact_email_lower ON bookings.bookings (lower(contact_email));

SELECT audit.fn_enable_audit('bookings.bookings', 'booking_id');

COMMENT ON TABLE bookings.bookings IS 'data-model/bookings/booking.md';

COMMIT;
