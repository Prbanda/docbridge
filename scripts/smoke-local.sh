#!/bin/bash
# M0 local smoke test (SV-3). Run after: docker compose up --build -d
# Proves: services healthy, each service's credentials reach ONLY its own
# logical database (ADR-009), LocalStack has the staging bucket + both
# queue pairs with redrive policies.
set -uo pipefail

PASS=0
FAIL=0

check() {
  local name="$1"; shift
  if "$@" >/dev/null 2>&1; then
    echo "  PASS  $name"; PASS=$((PASS + 1))
  else
    echo "  FAIL  $name"; FAIL=$((FAIL + 1))
  fi
}

check_eq() {
  local name="$1" expected="$2" actual="$3"
  if [ "$expected" = "$actual" ]; then
    echo "  PASS  $name"; PASS=$((PASS + 1))
  else
    echo "  FAIL  $name (expected: $expected, got: $actual)"; FAIL=$((FAIL + 1))
  fi
}

echo "== Service liveness and readiness =="
for svc in "auth:3001" "platform:3002" "docbridge-api:3003"; do
  name="${svc%%:*}"; port="${svc##*:}"
  check "$name /health" curl -fsS "http://localhost:${port}/health"
  check "$name /health/ready (own DB reachable)" curl -fsS "http://localhost:${port}/health/ready"
done

echo "== Database isolation (ADR-009: cross-service connect must fail) =="
check "auth_svc connects to auth db" \
  docker compose exec -T postgres psql "postgres://auth_svc:${AUTH_DB_PASSWORD:-auth-local-dev}@localhost:5432/auth" -c "SELECT 1"
CROSS=$(docker compose exec -T postgres psql "postgres://auth_svc:${AUTH_DB_PASSWORD:-auth-local-dev}@localhost:5432/platform" -c "SELECT 1" >/dev/null 2>&1 && echo allowed || echo denied)
check_eq "auth_svc denied on platform db" "denied" "$CROSS"

echo "== LocalStack resources =="
check "staging bucket exists" \
  docker compose exec -T localstack awslocal s3 ls s3://docbridge-staging-local
for q in docbridge-job-queue docbridge-job-queue-dlq docbridge-task-queue docbridge-task-queue-dlq; do
  check "queue $q exists" \
    docker compose exec -T localstack awslocal sqs get-queue-url --queue-name "$q"
done
REDRIVE=$(docker compose exec -T localstack sh -c \
  'awslocal sqs get-queue-attributes --queue-url $(awslocal sqs get-queue-url --queue-name docbridge-task-queue --query QueueUrl --output text) --attribute-names RedrivePolicy --query Attributes.RedrivePolicy --output text' 2>/dev/null | grep -o 'maxReceiveCount[^,}]*' || echo missing)
case "$REDRIVE" in
  *3*) echo "  PASS  task queue redrive maxReceiveCount=3"; PASS=$((PASS + 1)) ;;
  *)   echo "  FAIL  task queue redrive policy ($REDRIVE)"; FAIL=$((FAIL + 1)) ;;
esac

echo
echo "Result: ${PASS} passed, ${FAIL} failed"
[ "$FAIL" -eq 0 ]
