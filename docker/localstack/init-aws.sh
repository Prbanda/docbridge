#!/bin/bash
# LocalStack ready-hook: creates the staging bucket and both queue pairs
# (job + task, each with a DLQ and maxReceiveCount 3, ADR-005/008).
set -euo pipefail

REGION="${AWS_DEFAULT_REGION:-us-east-1}"

awslocal s3 mb "s3://docbridge-staging-local" --region "$REGION" || true

create_queue_pair() {
  local name="$1"
  local dlq_url dlq_arn
  dlq_url=$(awslocal sqs create-queue --queue-name "${name}-dlq" --query QueueUrl --output text)
  dlq_arn=$(awslocal sqs get-queue-attributes --queue-url "$dlq_url" \
    --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)
  awslocal sqs create-queue --queue-name "$name" --attributes "{
    \"RedrivePolicy\": \"{\\\"deadLetterTargetArn\\\":\\\"${dlq_arn}\\\",\\\"maxReceiveCount\\\":\\\"3\\\"}\"
  }"
}

create_queue_pair "docbridge-job-queue"
create_queue_pair "docbridge-task-queue"

echo "docbridge: LocalStack ready (staging bucket + job/task queues with DLQs)"
