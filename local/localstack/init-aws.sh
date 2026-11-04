#!/bin/bash
# LOCAL ONLY. Runs once LocalStack is ready: creates what Booking needs from the platform
# (00-overall-architecture.md section 12.4, 07-booking.md section 6). Idempotent: Payment's, Seat
# Inventory's and Fleet's own local scripts create some of the same topics and queues.
# The local stand-ins for the services that consume Booking's events are queues you can read by hand.
set -euo pipefail
export AWS_DEFAULT_REGION=ap-south-1
ACCOUNT=000000000000
QUEUES=http://localhost:4566/$ACCOUNT

# Topics (shared with the other services; creating one that exists is a no-op).
for topic in booking audit payment inventory fleet; do
  awslocal sns create-topic --name "routego-${topic}-events" >/dev/null
done

# A queue with its dead-letter queue: five receives, then parked.
make_queue() { # name dlq-name visibility-seconds
  awslocal sqs create-queue --queue-name "$2" >/dev/null
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$QUEUES/$2" --attribute-names QueueArn --query Attributes.QueueArn --output text)
  attributes=$(python3 -c 'import json,sys; print(json.dumps({"VisibilityTimeout": sys.argv[1], "ReceiveMessageWaitTimeSeconds": "20", "RedrivePolicy": json.dumps({"deadLetterTargetArn": sys.argv[2], "maxReceiveCount": "5"})}))' "$3" "$dlq_arn")
  awslocal sqs create-queue --queue-name "$1" --attributes "$attributes" >/dev/null
}

subscribe() { # queue topic filter-json
  attributes=$(python3 -c 'import json,sys; print(json.dumps({"RawMessageDelivery": "true", "FilterPolicy": json.dumps({"eventType": json.loads(sys.argv[1])})}))' "$3")
  awslocal sns subscribe --topic-arn "arn:aws:sns:ap-south-1:$ACCOUNT:routego-$2-events" \
    --protocol sqs --notification-endpoint "arn:aws:sqs:ap-south-1:$ACCOUNT:$1" \
    --attributes "$attributes" >/dev/null
}

# What Booking reads (queue catalog, section 10 of the overall architecture).
make_queue booking-payment-q booking-payment-dlq 60
subscribe booking-payment-q payment '["PaymentSucceeded","PaymentFailed","RefundCompleted"]'
make_queue booking-inventory-q booking-inventory-dlq 60
subscribe booking-inventory-q inventory '["SeatReleased"]'
make_queue booking-fleet-q booking-fleet-dlq 60
subscribe booking-fleet-q fleet '["TripScheduled","FareChanged","TripCancelled"]'

# Local stand-ins for Booking's subscribers (the real ones live in their own services), so a run can
# be inspected with: awslocal sqs receive-message --queue-url $QUEUES/<queue>
make_queue inventory-booking-q inventory-booking-dlq 60
subscribe inventory-booking-q booking '["BookingFailed","BookingCancelled"]'
make_queue payment-refund-q payment-refund-dlq 60
subscribe payment-refund-q booking '["RefundRequested"]'
make_queue ticket-q ticket-dlq 60
subscribe ticket-q booking '["BookingConfirmed","BookingSeatsChanged"]'
make_queue notification-q notification-dlq 60
subscribe notification-q booking '["BookingConfirmed","BookingSeatsChanged","BookingCancelled","BookingFailed","BookingExpired"]'
make_queue audit-q audit-dlq 60
subscribe audit-q audit '["AuditRecorded"]'
# Created last: the compose health check waits for it.
make_queue reporting-q reporting-dlq 60
subscribe reporting-q booking '["BookingConfirmed","BookingFailed","BookingExpired","BookingCancelled"]'

# Business settings (SSM Parameter Store). The hold is 60 seconds locally so the expiry demo is quick;
# production keeps the 600 seconds default. Every other setting is left unset, so the specs' defaults apply.
SETTINGS=/routego/prod/booking
awslocal ssm put-parameter --overwrite --type String --name "$SETTINGS/hold-seconds" --value 60 >/dev/null
awslocal ssm put-parameter --overwrite --type String --name "$SETTINGS/max-seats" --value 6 >/dev/null
awslocal ssm put-parameter --overwrite --type String --name "$SETTINGS/min-lead-minutes" --value 30 >/dev/null
awslocal ssm put-parameter --overwrite --type String --name "$SETTINGS/seat-change-limit" --value 3 >/dev/null
awslocal ssm put-parameter --overwrite --type String --name "$SETTINGS/force-cancel-window-days" --value 7 >/dev/null
awslocal ssm put-parameter --overwrite --type String --name "$SETTINGS/cancellation-tiers" \
  --value '[{"hoursBefore":24,"refundPercent":90},{"hoursBefore":6,"refundPercent":50},{"hoursBefore":0,"refundPercent":0}]' >/dev/null

# The internal key Booking presents to Seat Inventory and Payment (local-only throwaway value).
awslocal secretsmanager create-secret --name routego/internal-key --secret-string local-internal-key >/dev/null 2>&1 || true

echo "Booking queues, topics and settings are ready."
