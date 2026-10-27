#!/bin/bash
# Run the integration suites against a deployed environment, configured from Terraform outputs and
# Secrets Manager (nothing to copy by hand).
#
#   ./scripts/test-aws.sh dev                 # functional suite (M1-M4 + duplicate delivery + FILE_NOT_UPLOADED)
#   ./scripts/test-aws.sh dev --load          # + M5 load gates (p99 latency, isolation)
#   ./scripts/test-aws.sh dev --drills        # + M5 failure drills (scales the platform ECS service to 0 and back)
set -euo pipefail
cd "$(dirname "$0")/.."

ENV="${1:?usage: test-aws.sh <dev|prod> [--load] [--drills]}"
shift
out() { (cd "infra/envs/$ENV" && terraform output -raw "$1"); }
secret() { aws secretsmanager get-secret-value --secret-id "$1" --query SecretString --output text; }

export DOCBRIDGE_BASE_URL=$(out api_url)
export DOCBRIDGE_WS_URL=$(out ws_url)
export DOCBRIDGE_TASK_QUEUE_URL=$(out task_queue_url)
export DOCBRIDGE_TASK_DLQ_URL=$(aws sqs get-queue-url --queue-name "docbridge-$ENV-task-queue-dlq" --query QueueUrl --output text)
export DOCBRIDGE_ECS_CLUSTER=$(out cluster_name)
export PLATFORM_ADMIN_CLIENT_SECRET=$(secret "$(out platform_admin_secret_arn)")
export DELIVERY_WORKER_CLIENT_SECRET=$(secret "$(out delivery_worker_secret_arn)")
echo "Testing $ENV at $DOCBRIDGE_BASE_URL"

status=0
dotnet test --project tests/DocBridge.IntegrationTests || status=1
for arg in "$@"; do
  case "$arg" in
    --load) DOCBRIDGE_RUN_LOAD=1 dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5HardeningTests" || status=1 ;;
    --drills) DOCBRIDGE_DRILLS=aws dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5DrillTests" || status=1 ;;
  esac
done
exit $status
