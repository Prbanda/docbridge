#!/bin/bash
# LocalStack ready-hook: mirrors the AWS topology locally.
#   - staging bucket (browser uploads via presigned POST, so CORS for the local SPA origins)
#   - platform document bucket (the platform's own store, G3)
#   - job + task queues, each with a DLQ and maxReceiveCount 3 (ADR-005/008)
#     visibility: job 2 min, task 30 min (6x the 5-min delivery timeout, TDD 2.3)
set -euo pipefail

REGION="${AWS_DEFAULT_REGION:-us-east-1}"

awslocal s3 mb "s3://docbridge-staging-local" --region "$REGION" || true
awslocal s3 mb "s3://docbridge-platform-local" --region "$REGION" || true
awslocal s3api put-bucket-cors --bucket docbridge-staging-local --cors-configuration '{
  "CORSRules": [{
    "AllowedOrigins": ["http://localhost:8080", "http://localhost:5173"],
    "AllowedMethods": ["GET", "POST", "PUT"],
    "AllowedHeaders": ["*"],
    "ExposeHeaders": ["ETag"],
    "MaxAgeSeconds": 3600
  }]
}'

create_queue_pair() {
  local name="$1" visibility="$2"
  local dlq_url dlq_arn
  dlq_url=$(awslocal sqs create-queue --queue-name "${name}-dlq" --query QueueUrl --output text)
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$dlq_url" \
    --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)
  awslocal sqs create-queue --queue-name "$name" --attributes "{
    \"VisibilityTimeout\": \"${visibility}\",
    \"RedrivePolicy\": \"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"3\\\"}\"
  }"
}

create_queue_pair "docbridge-job-queue" 120
create_queue_pair "docbridge-task-queue" 1800

echo "docbridge: LocalStack ready (staging + platform buckets, job/task queues with DLQs)"
