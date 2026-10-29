#!/bin/bash
# One-command deploy of an environment (requirements: Operability; TDD 0.2/0.3). Same steps CI runs on main.
#
#   ./scripts/deploy.sh dev            # asks before each terraform apply
#   ./scripts/deploy.sh dev --yes      # non-interactive
#
# Steps: build Lambda packages → apply global (ECR, API GW account role) → build + push images tagged
# <git sha> and <env> → apply the environment → ship Lambda code → roll ECS services → publish the SPA with
# its runtime config (API + WebSocket URLs). Prereqs: aws CLI logged in, docker, terraform, dotnet 10, node 22,
# infra/envs/{global,<env>}/backend.hcl (docs/testing/M0-TESTING.md §2.2).
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)

ENV="${1:?usage: deploy.sh <dev|prod> [--yes]}"
APPROVE=""
[ "${2:-}" = "--yes" ] && APPROVE="-auto-approve"
REGION="${AWS_REGION:-us-east-1}"
ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
REGISTRY="$ACCOUNT.dkr.ecr.$REGION.amazonaws.com"
SHA=$(git rev-parse --short HEAD)
git diff --quiet HEAD -- . 2>/dev/null || SHA="$SHA-dirty-$(date +%Y%m%d%H%M%S)"

tf() { # tf <env-dir> <args...>
  local dir="infra/envs/$1"; shift
  (cd "$dir" && terraform init -input=false -reconfigure -backend-config=backend.hcl >/dev/null && terraform "$@")
}

echo "=== 1/7 build Lambda packages ==="
./scripts/build-lambdas.sh

echo "=== 2/7 apply global (ECR, API Gateway account settings) ==="
tf global apply -input=false $APPROVE

echo "=== 3/7 build + push images ($SHA → :$ENV) ==="
aws ecr get-login-password --region "$REGION" | docker login --username AWS --password-stdin "$REGISTRY" >/dev/null
for svc in auth platform docbridge-api; do
  image="$REGISTRY/docbridge/$svc"
  docker build -q -f "services/$svc/Dockerfile" -t "$image:$SHA" -t "$image:$ENV" . >/dev/null
  docker push -q "$image:$SHA" >/dev/null
  docker push -q "$image:$ENV" >/dev/null
  echo "pushed $image:$SHA (+:$ENV)"
done

echo "=== 4/7 apply $ENV ==="
tf "$ENV" apply -input=false $APPROVE
OUT=$(cd "infra/envs/$ENV" && terraform output -json)
out() { echo "$OUT" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const o=JSON.parse(s);console.log(eval('o.'+process.argv[1]+'.value'))})" "$1"; }

echo "=== 5/7 ship Lambda code ==="
for pkg in fanout delivery ws maintenance db-bootstrap; do
  for fn in $(echo "$OUT" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(JSON.parse(s).lambda_functions.value[process.argv[1]].join(' ')))" "$pkg"); do
    aws lambda update-function-code --function-name "$fn" --zip-file "fileb://dist/lambda/$pkg.zip" --query 'LastUpdateStatus' --output text >/dev/null
    aws lambda wait function-updated-v2 --function-name "$fn"
    echo "updated $fn"
  done
done

echo "=== 6/7 roll ECS services ==="
CLUSTER=$(out cluster_name)
for svc in auth platform docbridge-api; do
  aws ecs update-service --cluster "$CLUSTER" --service "$svc" --force-new-deployment --query 'service.serviceName' --output text >/dev/null
done
aws ecs wait services-stable --cluster "$CLUSTER" --services auth platform docbridge-api
echo "services stable"

echo "=== 7/7 publish SPA ==="
API_URL=$(out api_url)
WS_URL=$(out ws_url)
SPA_BUCKET=$(out spa_bucket_name)
(cd web && npm ci --no-audit --no-fund >/dev/null && npm run build >/dev/null)
printf 'window.DOCBRIDGE_CONFIG = { apiBaseUrl: "%s", wsUrl: "%s" };\n' "$API_URL" "$WS_URL" > web/dist/config.js
aws s3 sync web/dist "s3://$SPA_BUCKET" --delete --only-show-errors
# MSYS_NO_PATHCONV: stop Git Bash on Windows rewriting "/index.html" into a filesystem path.
MSYS_NO_PATHCONV=1 aws cloudfront create-invalidation --distribution-id "$(out spa_distribution_id)" --paths "/index.html" "/config.js" \
  --query 'Invalidation.Id' --output text >/dev/null

echo
echo "Deployed $ENV ($SHA)"
echo "  SPA:       $(out spa_url)"
echo "  API:       $API_URL"
echo "  WebSocket: $WS_URL"
echo "Seed demo data: ./scripts/seed.sh $ENV"
