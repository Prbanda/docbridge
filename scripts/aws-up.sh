#!/bin/bash
# Bring the dev environment up: applies global (ECR, ~free) then dev
# (VPC/NAT/RDS/proxy/S3/CloudFront — the billable part, ~$3-5/day).
# First run takes ~20-30 min; re-runs after aws-down.sh take about the same.
#
# Prereqs: backend.hcl present in infra/envs/{global,dev}
#          (see docs/testing/M0-TESTING.md §2.2)
set -euo pipefail
cd "$(dirname "$0")/../infra/envs"

for env in global dev; do
  echo "=== apply: $env ==="
  (
    cd "$env"
    # -reconfigure: pick up backend.hcl changes (e.g. DynamoDB lock → use_lockfile)
    terraform init -input=false -reconfigure -backend-config=backend.hcl
    terraform apply -input=false
  )
done

echo
echo "Dev environment is up. Reminder: run ./scripts/aws-down.sh when done practicing."
