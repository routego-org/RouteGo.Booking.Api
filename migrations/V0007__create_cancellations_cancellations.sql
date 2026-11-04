-- =============================================================================
-- V0007__create_cancellations_cancellations.sql
-- How one confirmed booking was cancelled: who started it, why, and what share comes back.
-- =============================================================================
-- Implements: data-model/cancellations/cancellation.md v0.1.0
-- Change tracking: audited (the default) - eight audit columns + audit.fn_enable_audit.
-- Identifier: keyed by the entity's identity, the booking (a booking is cancelled at most once).
-- Money: whole Indian rupees held directly in bigint.
-- cancelled_at is an entity attribute (when the booking was cancelled), distinct from the audit
--   columns, which record when the row was written.

BEGIN;

CREATE SCHEMA IF NOT EXISTS cancellations;

CREATE TABLE cancellations.cancellations (
    booking_id     uuid        NOT NULL,
    kind           text        NOT NULL,
    reason         text        NULL,
    refund_percent integer     NOT NULL,
    refund_amount  bigint      NOT NULL,
    cancelled_by   text        NULL,
    cancelled_at   timestamptz NOT NULL,

    -- Audit columns: populated by the audit trigger; declared NOT NULL here.
    created_by_id    text        NOT NULL,
    created_by_email text        NOT NULL,
    created_by_name  text        NOT NULL,
    created_at       timestamptz NOT NULL,
    updated_by_id    text        NOT NULL,
    updated_by_email text        NOT NULL,
    updated_by_name  text        NOT NULL,
    updated_at       timestamptz NOT NULL,
    CONSTRAINT pk_cancellations PRIMARY KEY (booking_id),
    -- Relationship: "Belongs to one Booking".
    CONSTRAINT fk_cancellations_booking_id FOREIGN KEY (booking_id) REFERENCES bookings.bookings (booking_id),
    -- Attribute kind: exactly the values the entity file lists.
    CONSTRAINT ck_cancellations_kind CHECK (kind IN ('CustomerCancelled', 'AdminCancelled', 'OperatorCancelled')),
    CONSTRAINT ck_cancellations_reason_len CHECK (reason IS NULL OR length(reason) <= 500),
    -- Attribute refund_percent: a whole number 0 to 100.
    CONSTRAINT ck_cancellations_refund_percent CHECK (refund_percent BETWEEN 0 AND 100),
    CONSTRAINT ck_cancellations_refund_amount CHECK (refund_amount >= 0),
    CONSTRAINT ck_cancellations_cancelled_by_len CHECK (cancelled_by IS NULL OR length(cancelled_by) <= 64),
    -- Rule: an operator cancellation always refunds 100%, and is started by no account of ours.
    CONSTRAINT ck_cancellations_operator_cancelled CHECK (kind <> 'OperatorCancelled' OR (refund_percent = 100 AND cancelled_by IS NULL)),
    -- Rule: an admin cancellation carries the admin's reason.
    CONSTRAINT ck_cancellations_admin_reason CHECK (kind <> 'AdminCancelled' OR reason IS NOT NULL),
    CONSTRAINT ck_cancellations_created_by_id_len CHECK (length(created_by_id) <= 64),
    CONSTRAINT ck_cancellations_updated_by_id_len CHECK (length(updated_by_id) <= 64)
);

SELECT audit.fn_enable_audit('cancellations.cancellations', 'booking_id');

COMMENT ON TABLE cancellations.cancellations IS 'data-model/cancellations/cancellation.md';

COMMIT;
