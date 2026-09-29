#!/bin/bash
# Tear down everything billable so no charges accrue between practice sessions.
#
# Destroys: infra/envs/dev — VPC, NAT gateway, RDS + proxy, S3 buckets
#           (force_destroy empties them), CloudFront. All dev data is lost;
#           that's fine, dev is disposable and reseedable.
# Keeps:    the Terraform state backend (S3 bucket, pennies/month) and
#           infra/envs/global ECR repos (storage cost only; pass --all to
#           destroy those too).
#
# Re-create everything later with ./scripts/aws-up.sh
set -euo pipefail
cd "$(dirname "$0")/../infra/envs"

echo "This DESTROYS the dev environment (all dev data). Type 'destroy' to continue:"
read -r answer
[ "$answer" = "destroy" ] || { echo "aborted"; exit 1; }

echo "=== destroy: dev ==="
(
  cd dev
  terraform init -input=false -reconfigure -backend-config=backend.hcl
  terraform destroy -input=false -auto-approve
)

if [ "${1:-}" = "--all" ]; then
  echo "=== destroy: global (ECR) ==="
  (
    cd global
    terraform init -input=false -reconfigure -backend-config=backend.hcl
    terraform destroy -input=false -auto-approve
  )
fi

echo
echo "Done. Remaining costs: Terraform state backend only (~pennies/month)."
