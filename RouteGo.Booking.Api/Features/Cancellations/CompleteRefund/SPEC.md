# CAP-CXL-005 — Complete refund

- Capability: CAP-CXL-005
- Canonical spec: `spec-RouteGo.Booking.API/capabilities/cancellations/features/refund-completion.md` (v0.1.1)
- Area: Cancellations
- Slice: `Features/Cancellations/CompleteRefund/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: refund-completed`, event type `RefundCompleted` from Payment (`CompleteRefundHandler : IEventWorker`).

- Event data (Payment's wire): `bookingId`, `amount` (JSON number, whole); `paymentId`, `refundId`, `currency`, `reason`, `customerEmail` ignored.
- Writes: `cancellations.refunds` (status, completed_at). Emits nothing.
- Worker alerts: `refundUnknown` (no refund, or unknown booking), `amountMismatch`, `invalidEventData`.

## Acceptance criteria

- [x] AC-1: A cancellation refund is completed — `HandleAsync_CancellationRefundReported_CompletesItWithTheTimeBookingLearnedOfIt`, `HandleAsync_AmountSentAsAWholeJsonNumberWithADecimalPoint_IsStillAccepted`
- [x] AC-2: A late-payment refund — `HandleAsync_LatePaymentRefundReported_CompletesItTheSameWay`
- [x] AC-3: Completed twice — `HandleAsync_CompletionDeliveredAgain_ChangesNothingAndKeepsTheCompletionTime`
- [x] AC-4: A completion Booking cannot match — `HandleAsync_BookingWithNoRefund_IsRejectedAsRefundUnknown`, `HandleAsync_UnknownBooking_IsRejectedAsRefundUnknown`, `HandleAsync_AmountDifferentFromTheRefund_IsRejectedAndTheRefundStaysRequested`, `HandleAsync_AmountDifferentFromACompletedRefund_IsRejectedAsAMismatch`, `HandleAsync_EventWithoutAnAmount_IsRejectedAsInvalidEventData`, `HandleAsync_AmountThatIsNotWhole_IsRejectedAsInvalidEventData`, `HandleAsync_DataThatIsNotAnObject_IsRejectedAsInvalidEventData`

## Invariants touched

- The refund row is read `FOR UPDATE`; only a `Requested` refund changes, so a repeat keeps the first completion time.
- The amount is checked against the refund before any change, for a Completed refund too.
