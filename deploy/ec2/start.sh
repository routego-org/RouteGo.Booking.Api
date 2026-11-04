#!/usr/bin/env bash
# Build the image, apply migrations, start the API, then wait for /health/ready.
# Usage on the one-server layout:  sudo bash ~/booking/deploy/ec2/start.sh
# Usage (three-instance layout, on routego-core):
#   sudo IDENTITY_JWKS_URL=http://<core-private-ip>:5001/.well-known/jwks.json \
#        IDENTITY_ISSUER=http://<core-private-ip>:5001 \
#        SEAT_INVENTORY_BASE_URL=http://<core-private-ip>:5004/ \
#        PAYMENT_BASE_URL=http://<secure-private-ip>:5006/ \
#        bash ~/booking/deploy/ec2/start.sh
set -euo pipefail

cd "$(dirname "$0")"
REGION=ap-south-1
DB_SECRET=database/postgresql/routego-booking-api
SETTINGS_PATH=/routego/prod/booking/

SHARED_NETWORK=ec2_default
POSTGRES_CONTAINER=ec2-postgres-1
POSTGRES_SUPERUSER=notification_user

# One-server layout: every RouteGo container shares the ec2_default network, so the other services are
# reached by name. Set a variable to override (the three-instance layout uses private addresses).
IDENTITY_JWKS_URL="${IDENTITY_JWKS_URL:-http://identity:8080/.well-known/jwks.json}"
IDENTITY_ISSUER="${IDENTITY_ISSUER:-$(aws ssm get-parameter --name /routego/prod/identity/issuer --region "$REGION" \
  --query Parameter.Value --output text)}"
SEAT_INVENTORY_BASE_URL="${SEAT_INVENTORY_BASE_URL:-http://seat-inventory:8080/}"
PAYMENT_BASE_URL="${PAYMENT_BASE_URL:-http://payment:8080/}"

if ! docker network inspect "$SHARED_NETWORK" >/dev/null 2>&1; then
  echo "ERROR: Docker network $SHARED_NETWORK not found. Is the Postgres container ($POSTGRES_CONTAINER) running?" >&2
  exit 1
fi

# The migrate container needs the connection details; the instance role reads them from the same
# secret the API uses, so the password is never written to a file on this server.
echo ">> Reading the database connection from Secrets Manager"
secret_json="$(aws secretsmanager get-secret-value --secret-id "$DB_SECRET" --region "$REGION" \
  --query SecretString --output text)"
read_field() {
  printf '%s' "$secret_json" | python3 -c 'import json, sys; print(json.load(sys.stdin).get(sys.argv[1], sys.argv[2]))' "$1" "${2:-}"
}
BOOKING_DB_HOST="$(read_field host)"
BOOKING_DB_PORT="$(read_field port 5432)"
BOOKING_DB_NAME="$(read_field database)"
BOOKING_DB_USER="$(read_field username)"
BOOKING_DB_PASSWORD="$(read_field password)"
BOOKING_DB_SSLMODE="$(read_field sslmode require)"
AWS_ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)"
export BOOKING_DB_HOST BOOKING_DB_PORT BOOKING_DB_NAME BOOKING_DB_USER BOOKING_DB_PASSWORD BOOKING_DB_SSLMODE
export AWS_ACCOUNT_ID IDENTITY_JWKS_URL IDENTITY_ISSUER SEAT_INVENTORY_BASE_URL PAYMENT_BASE_URL

# Booking refuses to start without its secret, and without its queues it could not hear payment, seat or
# trip events, so stop here with a clear message instead of a container that restarts forever.
echo ">> Checking the secrets, settings and queues the API needs"
if ! aws secretsmanager describe-secret --secret-id routego/internal-key --region "$REGION" >/dev/null 2>&1; then
  echo "ERROR: secret routego/internal-key is missing, or this instance role cannot read it." >&2
  exit 1
fi
if ! aws ssm get-parameters-by-path --path "$SETTINGS_PATH" --region "$REGION" >/dev/null 2>&1; then
  echo "ERROR: this instance role cannot read the settings under $SETTINGS_PATH (ssm:GetParametersByPath)." >&2
  exit 1
fi
for queue in booking-payment-q booking-inventory-q booking-fleet-q; do
  if ! aws sqs get-queue-url --queue-name "$queue" --region "$REGION" >/dev/null 2>&1; then
    echo "ERROR: queue $queue is missing, or this instance role cannot reach it." >&2
    exit 1
  fi
done

echo ">> Creating the database login and database (skipped when they exist)"
docker exec -i -e DB_NAME="$BOOKING_DB_NAME" -e DB_USER="$BOOKING_DB_USER" -e DB_PASSWORD="$BOOKING_DB_PASSWORD" \
  "$POSTGRES_CONTAINER" psql --no-psqlrc --quiet -U "$POSTGRES_SUPERUSER" -d postgres < create-database.sql

echo ">> Building image (first time takes 5-10 minutes)"
docker build -t routego/booking:latest ../..

echo ">> Applying migrations and starting the API"
if ! docker compose up -d; then
  echo "ERROR: migrations or API start failed. Last logs:" >&2
  docker compose ps -a
  docker compose logs --tail 50 migrate booking
  exit 1
fi

echo ">> Waiting for the API to be ready"
for _ in $(seq 1 30); do
  if curl -fs http://localhost:5005/health/ready >/dev/null 2>&1; then
    docker compose ps -a
    echo ">> DEPLOY OK — Booking API is ready on port 5005"
    exit 0
  fi
  sleep 2
done

echo "ERROR: Booking API did not become ready. Last logs:" >&2
docker compose ps -a
docker compose logs --tail 50 migrate booking
exit 1
