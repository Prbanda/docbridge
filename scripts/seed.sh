#!/bin/bash
# Seed demo users, teams, binders and nested folders (TDD 1.2) through the public APIs.
#   ./scripts/seed.sh local   → compose edge at http://localhost:8080
#   ./scripts/seed.sh dev     → the deployed API Gateway URL; admin secret read from Secrets Manager
# Creates Helios Therapeutics (Study HX-101/HX-204) and Nordlicht Clinical Sites (Site Berlin/Hamburg) with nested,
# empty folders; alice in both teams, bob in Helios only, carol in none. Emails use SEED_EMAIL_DOMAIN
# (default demo.docbridge.test). Re-running only adds what is missing; `docker compose down -v` resets local data.
# Add --perf to also create perf@<domain> with 50 teams (destination browser < 1 s check).
set -euo pipefail
cd "$(dirname "$0")/.."

TARGET="${1:?usage: seed.sh <local|dev|prod> [--perf]}"
shift
if [ "$TARGET" = "local" ]; then
  API="http://localhost:8080"
  export PLATFORM_ADMIN_CLIENT_SECRET="${PLATFORM_ADMIN_CLIENT_SECRET:-platform-admin-local-dev}"
else
  OUT_DIR="infra/envs/$TARGET"
  API=$(cd "$OUT_DIR" && terraform output -raw api_url)
  ARN=$(cd "$OUT_DIR" && terraform output -raw platform_admin_secret_arn)
  PLATFORM_ADMIN_CLIENT_SECRET=$(aws secretsmanager get-secret-value --secret-id "$ARN" --query SecretString --output text)
  export PLATFORM_ADMIN_CLIENT_SECRET
fi
export SEED_PASSWORD="${SEED_PASSWORD:-Demo-Password-2026}"
dotnet run --project tools/seed -- --api "$API" "$@"
