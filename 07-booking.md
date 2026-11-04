# 07. Booking Service

| | |
|---|---|
| **Type** | API + worker (the saga orchestrator) |
| **Build phase** | 7 (the capstone: all contracts come together) |
| **Port** | 5005 |
| **Data** | PostgreSQL `routego_booking`, user `booking_user` |
| **Instance** | `routego-core` |
| **Repo path** | `src/Booking` |

---

## 1. BRD: why it exists
- A booking spans several services (seats, payment, notifications), and none of them can be in one database transaction. Someone must own the end-to-end story and its failure handling (BO-2, BO-3).
- Business rules (hold duration, cancellation refund tiers, max seats) belong in one place and must be changeable without a deployment.

**Business outcome:** a booking that always ends in a clear state, with money and seats consistent.

## 2. PRD: what it offers
| Capability | Behavior |
|---|---|
| Create booking | Choose trip, seats, passengers; seats held; redirected to pay |
| Booking status | Poll while "Confirming your payment" |
| My bookings | List and detail (own bookings only) |
| Cancel | Refund amount shown before confirming; seats freed |
| Admin tools | Search any booking; force-cancel with reason |

Rules:
- Max 6 seats per booking; one passenger per seat (name, age, gender).
- Hold 10 minutes (SSM). Hold countdown is shown to the customer.
- Refund tiers (SSM): more than 24 h before departure 90%, 6 to 24 h 50%, under 6 h 0%.
- Cancellation only for Confirmed bookings before departure.
- Fares come from the trip snapshot, never from the browser.

## 3. FRD: how it behaves

### 3.1 State machine
`Initiated → SeatsHeld → PaymentPending → Confirmed | Failed | Expired`; `Confirmed → Cancelled`. Full table in the overall doc, section 3.2. Every transition is a conditional update (`UPDATE ... WHERE id=@id AND status=@expected`) so duplicate or racing events cannot move a booking twice.

### 3.2 Endpoints
| Method and path | Auth | Purpose |
|---|---|---|
| `POST /bookings` | Customer | `{tripId, seatNos[], passengers[]}` → `{bookingId, status, holdExpiresAt, checkoutUrl}` |
| `GET /bookings/{id}` | Owner or Admin | Status and details (used by return-page polling) |
| `GET /bookings?mine=true` | Customer | My bookings |
| `GET /bookings/{id}/cancel-preview` | Owner | Refund amount if cancelled now |
| `POST /bookings/{id}/cancel` | Owner | Cancel |
| `GET /admin/bookings?search=` | Admin | Search by id, email, trip |
| `POST /admin/bookings/{id}/force-cancel` | Admin | `{reason, refundPercent?}`; emits audit event |
| `GET /health`, `/health/ready` | none | Probes |

### 3.3 Create-booking flow
1. Validate: trip exists in `trip_snapshots`, status Scheduled, departure at least 30 minutes ahead, seat count within limit, seats exist in the snapshot. Compute amount from snapshot fares.
2. Insert booking `Initiated` and seats.
3. Call Inventory `POST /internal/trips/{id}/holds` with `holdSeconds` from SSM.
   - `409` → mark `Failed` (reason SeatUnavailable), return `409` with the unavailable seats.
   - Timeout or 5xx → call Inventory release (best effort), mark `Failed`, return `503`. (The expiry delay message cleans up anyway.)
4. Save `SeatsHeld`, `hold_expires_at`.
5. Call Payment `POST /internal/payments/intents`; failure → release seats, `Failed`.
6. Save `PaymentPending`, return `checkoutUrl`.

### 3.4 Payment results (consumer on `booking-payment-q`)
- `PaymentSucceeded`:
  - If booking is `PaymentPending` or `SeatsHeld`: call Inventory confirm. OK → `Confirmed` and outbox `BookingConfirmed`. Conflict (seat no longer held by this booking) → `Failed` and outbox `RefundRequested`.
  - If booking is `Expired` or `Failed`: outbox `RefundRequested` (reason LatePayment). **This is FR-E4.**
  - If already `Confirmed`: ignore (duplicate).
- `PaymentFailed`: `PaymentPending → Failed`; outbox `BookingFailed` (Inventory releases seats).
- `RefundCompleted`: record `refund_status=Completed` on the booking/cancellation.

### 3.5 Hold expiry (consumer on `booking-inventory-q`)
`SeatReleased` with reason `HoldExpired`: if booking is `SeatsHeld` or `PaymentPending` → `Expired`, outbox `BookingExpired`.

### 3.6 Trip snapshots (consumer on `booking-fleet-q`)
`TripScheduled` upserts; `FareChanged` updates fares (does **not** change existing bookings); `TripCancelled` marks cancelled. Optional extension: cancel all Confirmed bookings for a cancelled trip with 100% refund (reason OperatorCancelled).

### 3.7 Cancellation
1. Check status `Confirmed` and departure in the future.
2. Read tiers from SSM (cached for 60 s), compute refund from hours-to-departure.
3. One transaction: set `Cancelled`, insert `cancellations` row, outbox `BookingCancelled` (always) and `RefundRequested` (if refund greater than 0, idempotency key = `bookingId:cancel`), outbox `AuditRecorded` for force-cancels.
4. Inventory releases the seats on `BookingCancelled`. Payment refunds. Notification emails.

### 3.8 Data model
```
bookings(id uuid pk, user_id, user_email, user_name, trip_id, status, total_amount, currency,
         hold_expires_at, payment_id, failure_reason, version int, created_at, updated_at)
booking_seats(booking_id, seat_no, passenger_name, passenger_age, passenger_gender, fare)
cancellations(booking_id pk, requested_by, refund_percent, refund_amount, reason, refund_status, created_at)
trip_snapshots(trip_id pk, operator_id, operator_name, operator_email, source, destination,
               stops jsonb, departure_utc, arrival_utc, bus_type, seat_fares jsonb, status)
outbox(id, event_type, payload jsonb, created_at, published_at)
processed_messages(message_id pk, processed_at)
```

### 3.9 Events
| Direction | Event | Notes |
|---|---|---|
| Consumes | `PaymentSucceeded`, `PaymentFailed`, `RefundCompleted` | `booking-payment-q` |
| | `SeatReleased` | `booking-inventory-q` |
| | `TripScheduled`, `FareChanged`, `TripCancelled` | `booking-fleet-q` |
| Publishes `routego-booking-events` | `BookingConfirmed` | **Full ticket snapshot**: booking id, customer name and email, operator name and email, route, times, bus type, seats with passengers, amount |
| | `BookingFailed`, `BookingExpired`, `BookingCancelled`, `RefundRequested` | |
| Publishes `routego-audit-events` | `AuditRecorded` | Force-cancel, cancellation |

### 3.10 Config and metrics
| Where | Key |
|---|---|
| SSM `/routego/prod/booking/` | `hold-seconds` (600), `max-seats` (6), `cancellation-tiers` (`[{"hoursBefore":24,"refundPercent":90},{"hoursBefore":6,"refundPercent":50},{"hoursBefore":0,"refundPercent":0}]`), `min-lead-minutes` (30), `inventory-url`, `payment-url` |
| Secrets | `routego/booking/db`, `routego/internal/api-key` |
| CloudWatch custom metrics (namespace `RouteGo/Booking`, written as embedded metric format log lines) | `BookingsConfirmed`, `HoldsExpired`, `LateRefunds`, `PaymentToConfirmSeconds` |

## 4. Dependencies
**Depends on (sync):**
| Service | For what |
|---|---|
| Seat Inventory | Hold, confirm, release (answer needed now) |
| Payment | Create payment intent |

**Consumes (async):** Payment, Inventory, Fleet events.

**Depended on by:**
| Who | For what |
|---|---|
| Web Portal | Create, status, list, cancel, admin search |
| Ticket Generator | `BookingConfirmed` |
| Notification | `BookingConfirmed`, `BookingCancelled` |
| Inventory | `BookingFailed`, `BookingCancelled` (release) |
| Payment | `RefundRequested` |
| Reporting | All booking events |
| Audit | `AuditRecorded` |

## 5. AWS services used and why
| AWS service | Use | Without it |
|---|---|---|
| **SNS** | Publish booking events to many consumers | Booking must call each consumer and fail when one is down |
| **SQS (+ DLQ)** | Receive payment, inventory, fleet events | A payment event arriving during a deployment is lost; the customer paid for nothing |
| **SSM Parameter Store** | Hold time, tiers, limits | Rule changes need a redeploy; business cannot tune safely |
| **CloudWatch custom metrics and alarms** | Business health numbers | You only learn about problems from customers |
| **Secrets Manager** | DB password, internal key | Credentials in config |
| **IAM** | Limited to its queues, topics, parameters | n/a |

**What students learn:** the saga pattern (orchestration), the outbox pattern, compensating actions (refund, release), why "retry plus idempotency" beats distributed transactions, conditional state transitions, and feature flags through parameter store.

## 6. Before building: AWS setup
1. DB `routego_booking`, user, secret `routego/booking/db`.
2. Queues `booking-payment-q`, `booking-inventory-q`, `booking-fleet-q` (+ DLQs) and subscriptions with the filters in the queue catalog. Verify `inventory-booking-q` and `payment-refund-q` also subscribe to `routego-booking-events` (set in Phases 5 and 6).
3. Topics `routego-booking-events`, `routego-audit-events`.
4. SSM parameters from section 3.10 (store tiers as a JSON string).
5. Core role: SQS on three queues; SNS publish on booking and audit topics; SSM read; CloudWatch `PutMetricData` only if you use the API (EMF through logs needs no extra permission).
6. ECR repo `routego/booking`; log group; CloudWatch alarms for DLQs and for `LateRefunds`.
7. **Prerequisite services running:** Identity, Fleet, Inventory, Payment (with the Webhook Receiver).

## 7. Local development
1. `docker compose up -d`; start Identity, Fleet, Inventory, Payment, MockGateway and the local webhook Lambda (or the MockGateway direct-to-queue shortcut).
2. Local SSM parameters (script `infra/local/localstack-init/03-booking.sh`) with `hold-seconds=60` so the expiry demo is quick.
3. `dotnet run --project src/Booking -- --migrate-only`, then `dotnet run --project src/Booking`.
4. End-to-end: operator creates trip → customer logs in → `POST /bookings` → open `checkoutUrl` → Success → poll `GET /bookings/{id}` until `Confirmed` → see `BookingConfirmed` in `ticket-q`.
5. Test the failure paths locally:
   - Do not pay; wait 60 s; status `Expired`.
   - Pay after expiry (MockGateway Success): `RefundRequested` appears; Payment refunds; `RefundCompleted` arrives.
   - Two customers on the same seat: one `409`.
   - Change `cancellation-tiers` in the local SSM and cancel again to see different refunds with no restart (cache refresh within 60 s).

## 8. Deploying to AWS
1. Prerequisites above, and Inventory and Payment already deployed.
2. DB, user, secret.
3. Build and push `routego/booking`.
4. SSM parameters (use the private addresses of core and secure for `inventory-url`, `payment-url`).
5. Migrate.
6. Compose block on core (`5005:8080`, log group `/routego/booking`).
7. Nginx: expose nothing directly (Portal calls over the private network). Optionally expose `/api/bookings` through API Gateway as the throttling lab.
8. Verify the whole product flow from the Portal; check CloudWatch metrics `BookingsConfirmed` in `RouteGo/Booking`; confirm all queues are at 0.
9. **Break it on purpose:**
   - **Success criterion 1:** 100 parallel bookings for one seat: one success.
   - **Success criterion 4:** hold a seat and wait 10 minutes: released and booking `Expired`.
   - **Success criterion 5:** pay after expiry: automatic refund.
   - Stop Booking during a payment; restart; the booking confirms from the queue.
   - Change the hold time in SSM from 600 to 120 and show the effect with no deployment.
   - Kill Booking between "save booking" and "publish event" (use a debug hook): the outbox row is still published after restart.

## 9. Lab checklist
- [ ] All six success criteria from the overall doc pass.
- [ ] Duplicate `PaymentSucceeded` leaves one `BookingConfirmed`.
- [ ] Students can draw the saga and say what compensates what.
