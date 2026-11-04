# RouteGo.Booking.Api

The Booking service of RouteGo: it owns a customer's booking from the moment they choose seats to the moment it
ends (confirmed, failed, expired or cancelled) and keeps seats and money consistent across Seat Inventory and
Payment, which share no database with it. Every successful payment ends as a confirmed booking or an automatic
refund. API plus background workers; no screen (the Web Portal has them), no public route.

- Product specs: `spec-RouteGo.Booking.API` (the source of truth; `00-overall-architecture.md` and `07-booking.md` here are the system documents it was derived from, and the specs win where they differ, see its ADRs 0002 and 0005 to 0010)
- Port 5005, database `routego_booking`, runs on `routego-core`
- Register of what is built: `specs/INDEX.md`

## What it does

| Area | Capability | How it is reached |
|---|---|---|
| Bookings | `CreateBooking`, `GetBooking`, `ListBookings`, `ReleaseBooking`, `CreateSeatChange`, `SearchBookings`, `ListTripPassengers` | HTTP with an Identity access token (Customer, Operator or Admin) |
| Bookings | `ApplyPaymentSucceeded`, `ApplyPaymentFailed`, `ExpireBooking` | queues `booking-payment-q`, `booking-inventory-q` |
| Bookings | `PublishBookingEvents` | every 5 seconds, from the transactional outbox |
| Cancellations | `PreviewBookingCancellation`, `CancelBooking`, `CreateForceCancellation` | HTTP |
| Cancellations | `CancelTripBookings`, `CompleteRefund` | queues `booking-fleet-q`, `booking-payment-q` |
| TripSnapshots | `CreateTripSnapshot`, `UpdateTripFares` | queue `booking-fleet-q` |

Health: `GET /health` (liveness), `GET /health/ready` (database and the three event queues).

## Layout

```
RouteGo.Booking.Api/          the service (Features/{Area}/{Action}/ vertical slices, Domain/, Common/, Infrastructure/)
RouteGo.Booking.Api.Tests/    tests: real Postgres (Testcontainers), the real host, fakes for Seat Inventory and Payment
RouteGo.Booking.Api.AppHost/  dev-only Aspire dashboard
migrations/                   raw SQL, applied by the pipeline's DbUp step (never by the app)
local/  docker-compose.yml    a local environment: Postgres, migrations, LocalStack queues/topics/settings
deploy/ec2/                   deployment to routego-core
docs/coding-standards/        the build-enforced RouteGo C# standard (submodule)
.agents/skills/               the framework skills this repo is built with (submodules)
```

## Run it locally

```bash
docker compose up -d postgres migrate localstack      # dependencies
dotnet user-secrets set "database/postgresql/routego-booking-api" '{"host":"localhost","port":5434,"database":"routego_booking","username":"booking_user","password":"booking_local","sslmode":"disable"}' --id routego-platform
dotnet user-secrets set "routego/internal-key" "local-internal-key" --id routego-platform
dotnet run --project RouteGo.Booking.Api              # http://localhost:5005 (Scalar docs at /)
```

Identity (:5001), Seat Inventory (:5004) and Payment (:5006) run from their own repos. `LocalEntryPoints` points
this service at them and at LocalStack.

## Configuration

Infrastructure addresses are environment variables named once in `Infrastructure/ConfigKeys.cs`. Secrets
(`database/postgresql/routego-booking-api`, `routego/internal-key`) come from user-secrets locally and Secrets
Manager when deployed. **Business settings are not environment variables:** they live in SSM Parameter Store under
`/routego/prod/booking/` and are re-read every 60 seconds, so a change needs no restart or release.

| Parameter | Default | Meaning |
|---|---|---|
| `hold-seconds` | 600 | how long Seat Inventory holds seats while the customer pays (max 900) |
| `max-seats` | 6 | seats per booking (max 6) |
| `min-lead-minutes` | 30 | booking and seat changes close this long before departure |
| `seat-change-limit` | 3 | seat changes per booking |
| `force-cancel-window-days` | 7 | days after departure an admin may still force-cancel |
| `cancellation-tiers` | `[{"hoursBefore":24,"refundPercent":90},{"hoursBefore":6,"refundPercent":50},{"hoursBefore":0,"refundPercent":0}]` | refund schedule |

## Tests

```bash
dotnet test RouteGo.Booking.Api.Tests/RouteGo.Booking.Api.Tests.csproj    # needs Docker (Testcontainers Postgres)
```
