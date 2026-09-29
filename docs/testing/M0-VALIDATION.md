# M0 validation runbook

Step-by-step acceptance check for Milestone 0. Run top to bottom after the
implementation is in place (setup: `docs/SETUP.md`). Every step has a pass
criterion; M0 is done only when **all** phases pass. Estimated total time:
~45 min, most of it waiting on Terraform.

Prereqs: Node 22, Terraform ≥ 1.15.8, Docker (Phase 2 only), AWS CLI configured,
`backend.hcl` files present in `infra/envs/{global,dev,prod}`.

## Phase 1 — Static checks (no AWS, no Docker; ~3 min)

| # | Command (repo root) | Pass criterion |
|---|---|---|
| 1.1 | `npm ci` | exits 0 |
| 1.2 | `npm run typecheck` | exits 0, no TS errors |
| 1.3 | `npm run lint` | exits 0 |
| 1.4 | `npm test` | all unit tests pass (24+ across shared, services, workers) |
| 1.5 | `npm run build --workspaces --if-present` | shared lib + 3 worker bundles build |
| 1.6 | `terraform fmt -check -recursive infra/` | no output, exits 0 |
| 1.7 | `for e in global dev prod; do (cd infra/envs/$e && terraform init -backend=false -input=false >/dev/null && terraform validate); done` | `Success!` ×3 |
| 1.8 | `gitleaks detect --source . --no-banner` (or rely on CI) | no leaks found |

## Phase 2 — Local integration (Docker required; ~5 min)

| # | Command | Pass criterion |
|---|---|---|
| 2.1 | `cp .env.example .env` (first time only) | — |
| 2.2 | `docker compose up --build -d` | all containers reach healthy |
| 2.3 | `./scripts/smoke-local.sh` | script exits 0; checks: 3 services answer `/health` and `/health/ready`, each service sees only its own logical DB, LocalStack has the staging bucket + 4 queues |
| 2.4 | `docker compose down -v` | clean shutdown |

## Phase 3 — Live AWS (~30 min up, checks, teardown optional)

| # | Command | Pass criterion |
|---|---|---|
| 3.1 | `./scripts/aws-up.sh` | both applies finish with no errors |
| 3.2 | `cd infra/envs/dev && terraform plan -input=false` | `No changes.` (config matches reality — no drift) |
| 3.3 | `aws ecr describe-repositories --query 'repositories[].repositoryName'` | `docbridge/auth`, `docbridge/platform`, `docbridge/docbridge-api` |
| 3.4 | `aws rds describe-db-instances --db-instance-identifier docbridge-dev --query 'DBInstances[0].{pub:PubliclyAccessible,enc:StorageEncrypted,st:DBInstanceStatus}'` | `pub: false`, `enc: true`, `st: available` |
| 3.5 | `aws rds describe-db-proxies --db-proxy-name docbridge-dev --query 'DBProxies[0].{st:Status,tls:RequireTLS}'` | `st: available`, `tls: true` |
| 3.6 | `aws rds describe-db-proxy-targets --db-proxy-name docbridge-dev --query 'Targets[0].TargetHealth.State'` | `AVAILABLE` — a proxy can be `available` while its target is unhealthy (bad secret, auth failure); this is the check that catches it before M2's Lambdas do |
| 3.7 | `aws s3api get-bucket-encryption --bucket docbridge-dev-staging-<ACCT>` | SSE algorithm `aws:kms` |
| 3.8 | `aws s3api get-public-access-block --bucket docbridge-dev-staging-<ACCT>` | all four flags `true` |
| 3.9 | `aws s3api get-bucket-lifecycle-configuration --bucket docbridge-dev-staging-<ACCT>` | rule enabled: expire 30d, abort multipart 7d |
| 3.10 | `aws s3 ls s3://docbridge-dev-staging-<ACCT>` then the same with `--endpoint-url http://s3.amazonaws.com` | HTTPS call succeeds; the plain-HTTP call fails with `explicit deny in a resource-based policy`. (An anonymous curl is not a valid check — it gets `AccessDenied` even without the TLS-only policy.) |
| 3.11 | `curl -sI $(cd infra/envs/dev && terraform output -raw spa_url)` | responds over HTTPS (403 expected until the SPA ships in M1) |
| 3.12 | `aws secretsmanager list-secrets --query 'SecretList[].Name'` | `docbridge/dev/db/{master,auth,platform,docbridge}` |

## Phase 4 — CI green on `main`

Requires one-time wiring (SETUP.md Part A: OIDC role + repo variables + `dev`/`prod` environments).

| # | Check | Pass criterion |
|---|---|---|
| 4.1 | `gh run list --branch main --limit 10` (or Actions tab) | latest run of every workflow green |
| 4.2 | `gitleaks` workflow | passes (CLI scan, no license needed) |
| 4.3 | 3 × `service-*` workflows | `check` + `build-push` green; image with the commit SHA tag visible in ECR (`aws ecr list-images --repository-name docbridge/auth`) |
| 4.4 | `workers` workflow | `check` green, bundle artifact uploaded; `deploy-dev` skipped (gate var unset until M2) |
| 4.5 | `infra` workflow | `validate` + `apply-dev` green; `apply-prod` waiting on manual approval is OK |

## Phase 5 — Teardown (cost rule)

| # | Command | Pass criterion |
|---|---|---|
| 5.1 | `./scripts/aws-down.sh` | destroy completes, ~49 resources |
| 5.2 | `aws rds describe-db-instances --query 'length(DBInstances)'` | `0` |
| 5.3 | `aws ec2 describe-nat-gateways --filter Name=state,Values=available,pending --query 'length(NatGateways)'` | `0` |
| 5.4 | `aws ec2 describe-vpcs --filters Name=is-default,Values=false --query 'length(Vpcs)'` | `0` |
| 5.5 | `aws secretsmanager list-secrets --query 'SecretList[?starts_with(Name, \`docbridge/dev/db\`)].Name'` | empty — secrets are deleted immediately (`recovery_window_in_days = 0` in the RDS module), so a same-week `aws-up.sh` won't hit "secret scheduled for deletion" |

Intentionally surviving teardown (≈ pennies/month): state bucket, empty ECR
repos. No DynamoDB lock table (S3-native `use_lockfile`).

## Sign-off

M0 is complete when: Phases 1–3 pass locally, Phase 4 is green on `main`,
and Phase 5 confirms zero billable leftovers. Record date + commit SHA below.

| Date | Commit | Phases passed | Notes |
|---|---|---|---|
| 2026-07-29 | d5d05a4+ | 1, 3 (re-verified) | Authenticated TLS-only check (HTTPS ok / HTTP explicit deny); proxy target `AVAILABLE`; secrets `recovery_window_in_days = 0` in TF. Phase 2 still blocked (no Docker); Phase 4 still needs GitHub repo variables. |
| 2026-07-24 | (pre-push) | 1, 3, 5 | first live AWS cycle; anonymous TLS curl was a false-positive check (fixed above) |
