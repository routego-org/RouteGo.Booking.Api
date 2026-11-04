-- =============================================================================
-- V0008__create_cancellations_refunds.sql
-- Money Booking asked Payment to return for one booking, and whether Payment has returned it.
-- =============================================================================
-- Implements: data-model/cancellations/refund.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: keyed by the entity's identity, the booking (a booking has at most one refund).
-- Money: whole Indian rupees held directly in bigint.
-- payment_id is Payment's identifier for the payment refunded: an external reference, no foreign key.

BEGIN;

CREATE TABLE cancellations.refunds (
    booking_id   uuid        NOT NULL,
    payment_id   uuid        NOT NULL,
    reason       text        NOT NULL,
    amount       bigint      NOT NULL,
    refund_key   text        NOT NULL,
    status       text        NOT NULL,
    requested_at timestamptz NOT NULL,
    completed_at timestamptz NULL,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_refunds PRIMARY KEY (booking_id),
    -- Relationship: "Belongs to one Booking".
    CONSTRAINT fk_refunds_booking_id FOREIGN KEY (booking_id) REFERENCES bookings.bookings (booking_id),
    -- Attribute reason: exactly the values the entity file lists.
    CONSTRAINT ck_refunds_reason CHECK (reason IN ('Cancellation', 'LatePayment', 'SeatsLost', 'TripCancelled')),
    -- Attribute amount: whole rupees, more than 0.
    CONSTRAINT ck_refunds_amount CHECK (amount >= 1),
    -- Attribute refund_key: "<bookingId>:cancel" for a cancellation, "<bookingId>:payment" otherwise.
    CONSTRAINT ck_refunds_refund_key_len CHECK (length(refund_key) <= 100),
    CONSTRAINT ck_refunds_refund_key_matches_reason CHECK (
        refund_key = booking_id::text || CASE WHEN reason = 'Cancellation' THEN ':cancel' ELSE ':payment' END),
    -- Attribute status: exactly the values the entity file lists; completed_at present exactly when Completed.
    CONSTRAINT ck_refunds_status CHECK (status IN ('Requested', 'Completed')),
    CONSTRAINT ck_refunds_completed_at CHECK ((status = 'Completed') = (completed_at IS NOT NULL)),
    CONSTRAINT ck_refunds_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_refunds_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

SELECT audit.fn_enable_audit('cancellations.refunds', 'booking_id');

COMMENT ON TABLE cancellations.refunds IS 'data-model/cancellations/refund.md';

COMMIT;
