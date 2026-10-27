# M5 Testing: Hardening and end-to-end verification

**Milestone claim (TDD M5):** the Blueprint's success metrics hold on the deployed system, and gate the prod apply.

## 1. The gates

| # | Gate (TDD M5 / Blueprint success metrics) | How | Where |
| :-- | :-- | :-- | :-- |
| G-1 | p99 submission latency < 500 ms on 100 × 20 = 2,000-task jobs | `M5HardeningTests.Submission_p99_stays_under_500ms_for_100_by_20_jobs` (10 runs by default, `DOCBRIDGE_P99_RUNS`) | local + AWS |
| G-2 | Partial-failure isolation: one revoked-membership destination → 1 failed / 1,900 completed | `M5HardeningTests.One_revoked_destination_fails_one_task_and_the_rest_complete` | local + AWS |
| G-3 | Platform killed mid-run → retries drain the backlog on recovery, DLQ stays empty | `M5DrillTests.Platform_outage_shorter_than_the_retry_window…` | local + AWS |
| G-4 | Outage longer than every retry → each task visibly `RETRIES_EXHAUSTED`, never silently lost | `M5DrillTests.Platform_outage_longer_than_all_retries…` | local + AWS |
| G-5 | Commit-then-publish failure → the sweeper republishes | `M5DrillTests.Job_committed_without_its_queue_message…` | local |
| G-6 | Never-uploaded file surfaces `FILE_NOT_UPLOADED` | `M2DistributeTests.Never_uploaded_file_fails_its_task_with_file_not_uploaded` | local + AWS |
| G-7 | At-least-once duplicates → exactly one document per task | `M5HardeningTests.Duplicate_task_messages_produce_exactly_one_document_per_task` | local + AWS |
| G-8 | Security: unauthenticated 401 at the gateway, cross-team 404s, expired presigned URLs fail, gitleaks clean | M1/M3/M4 suites + §3.2 + `gitleaks` | local + AWS |
| G-9 | Destination browser < 1 s with 50 teams | `M1BrowseTests.Destination_browser_loads_50_teams_well_under_one_second` | local + AWS |
| G-10 | End-to-end: N files × M destinations → all tasks completed, in local and cloud environments | the whole integration suite | local + AWS |
| G-11 | Manual: throttled-connection upload UX, multipart within-session resume, WS reconnect | §4 | browser |

## 2. Local run

```bash
docker compose up --build -d && ./scripts/smoke-local.sh
dotnet test --project tests/DocBridge.UnitTests
dotnet test --project tests/DocBridge.IntegrationTests                         # G-6…G-10 (load and drills skip)
DOCBRIDGE_RUN_LOAD=1 dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5HardeningTests"   # G-1, G-2, G-7
./scripts/drill-local.sh                                                         # G-3, G-4, G-5
docker run --rm -v "$PWD:/repo" zricethezav/gitleaks:v8.21.2 detect --source /repo --no-banner --redact
```

Run the load gates alone: they push 20,000+ deliveries through the stack, and anything running in parallel adds
latency noise to G-1.

## 3. AWS (dev) run

```bash
./scripts/deploy.sh dev
cd infra/envs/dev
export DOCBRIDGE_BASE_URL=$(terraform output -raw api_url)
export DOCBRIDGE_WS_URL=$(terraform output -raw ws_url)
export DOCBRIDGE_TASK_QUEUE_URL=$(terraform output -raw task_queue_url)
export DOCBRIDGE_TASK_DLQ_URL=$(aws sqs get-queue-url --queue-name docbridge-dev-task-queue-dlq --query QueueUrl --output text)
export PLATFORM_ADMIN_CLIENT_SECRET=$(aws secretsmanager get-secret-value --secret-id "$(terraform output -raw platform_admin_secret_arn)" --query SecretString --output text)
export DELIVERY_WORKER_CLIENT_SECRET=$(aws secretsmanager get-secret-value --secret-id "$(terraform output -raw delivery_worker_secret_arn)" --query SecretString --output text)
cd ../../..
dotnet test --project tests/DocBridge.IntegrationTests
DOCBRIDGE_RUN_LOAD=1 dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5HardeningTests"
DOCBRIDGE_DRILLS=aws dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5DrillTests"
```

### 3.2 Security spot checks (G-8)

```bash
API=$DOCBRIDGE_BASE_URL
curl -s -o /dev/null -w '%{http_code}\n' "$API/jobs"                                   # 401 from the gateway
curl -s -o /dev/null -w '%{http_code}\n' -H 'Authorization: Bearer x.y.z' "$API/jobs"  # 401
aws elbv2 describe-load-balancers --names docbridge-dev-internal --query 'LoadBalancers[0].Scheme'   # internal
aws s3api get-public-access-block --bucket <staging bucket>                            # all true
```

Expired presigned URL: see M4-TESTING §3.

## 4. Manual checks (G-11)

| Check | How | Expected |
| :-- | :-- | :-- |
| Throttled upload UX | Chrome DevTools → Network → "Slow 4G", upload 10 files | progress bars move steadily, UI stays responsive (hashing runs in a Web Worker) |
| Multipart within-session resume | Upload a > 100 MB PDF; go offline mid-upload, then back online | the failed part retries; completed parts aren't re-sent; file ends `uploaded` |
| WS reconnect | On a running job, `docker compose restart workers-local` (local) or toggle the network (AWS) | badge `○ reconnecting` → `● live`, counts re-sync |

## 5. Results of the agent's testing round (2026-10-22)

Measured values come from `%TEMP%/docbridge-metrics.txt` (written by the tests) and, for AWS ack latency, from the
HTTP API access logs (`/apigw/docbridge-dev-http`, server-side `latencyMs`).

### 5.1 Local (docker compose)

| Suite | Result |
| :-- | :-- |
| Smoke (`smoke-local.sh`) | 20 / 20 |
| Unit (`DocBridge.UnitTests`) | 51 passed, 0 failed |
| Web unit (`vitest`) | 3 passed |
| Integration (M1–M4 + M2 ingest + M5 duplicates) | 37 passed, 0 failed, 5 skipped (the opt-in load/drill tests) |
| Load gates (G-1, G-2, G-7) | 3 / 3 — p99 **329 ms** (median 204 ms) over 10 × 2,000-task submissions on the final code (earlier runs: 277 ms, 435 ms); isolation **1 failed / 1,900 completed** |
| Drills (G-3, G-4, G-5) | 3 / 3 — 400-task outage drained with 0 failures; every undelivered task `RETRIES_EXHAUSTED` after exactly 3 attempts; sweeper republished a lost job message |
| `dotnet format --verify-no-changes`, `terraform validate` ×3, `terraform fmt` | clean |
| gitleaks (working tree) | 2 findings, both false positives (a Terraform reference, a test-user password), marked `gitleaks:allow` |

### 5.2 AWS dev (`./scripts/deploy.sh dev`, then `./scripts/test-aws.sh dev --load --drills`)

| Gate / suite | Result |
| :-- | :-- |
| Deploy | 160 resources created by Terraform (incl. DB bootstrap invocation), 3 images, 9 Lambdas, ECS 2/2 × 3, SPA on CloudFront |
| Integration suite | 36 + 1 (M3 push test, re-run after a test-only fix) passed; 0 product failures |
| G-1 p99 submission (10 × 2,000 tasks) | **Final run, db.t4g.small (prod's class): client-side p99 257 ms, median 197 ms; gateway 105–184 ms.** History: 364 ms on day-1 tables; then 800 ms–2 s once the tables grew past the 1 GB db.t4g.micro's cache under a 58,000-task backlog. Fixed by the §5.3 changes plus moving dev to the prod DB class. Cold path on freshly rolled tasks: 3.4–4.1 s originally; now the ALB waits for warm-up |
| G-2 isolation | **1 failed / 1,900 completed** |
| G-3 platform outage (ECS scaled 0 → 2) | passed: 400 tasks, retries observed, 0 failed, DLQ unchanged |
| G-4 outage longer than every retry | passed: delivery trigger paused while the job was accepted, platform scaled to 0, trigger resumed → every task `RETRIES_EXHAUSTED` after exactly 3 attempts (≈ 11 min with the 60 s / 300 s backoff) |
| G-7 duplicate task messages | passed: 2 extra copies of every message injected into the real SQS queue → exactly 1 document per task |
| RDS reboot resilience | 18/18 API requests 200, 30 s after the instance came back |
| G-8 security | gateway 401 without / with a bogus token; ALB scheme `internal`; staging + platform buckets SSE-KMS + Bucket Key, all four public-access blocks on; WS access-log format has no query string; presigned GET expired after 2 min (**403 Request has expired**) on real S3; download host is the S3 bucket |
| G-9 50-team browse | max **118 ms**, median 48 ms |

### 5.3 Defects found and fixed during the round

| Found by | Defect | Fix |
| :-- | :-- | :-- |
| Integration (local) | HTTP content disposed the hashing stream; worker crashed after a successful ingest and looped on redelivery | digest fixed at end of stream; any non-permanent error now goes through retry → DLQ, never stuck |
| Integration (local) | `text[]` column mapping crashed `/auth/token` | property-mapped row class |
| Integration (local) | download presigned URL used https against LocalStack | URL scheme follows the presigner endpoint |
| Integration (AWS) | missing staged file answered 403 (no ListBucket), retried instead of `FILE_NOT_UPLOADED` | delivery role gets `s3:ListBucket` scoped to `staging/` |
| Gate G-1 (AWS) | ~4 s first submission on a new task (JIT, first DB connection, JWKS, service token) | ReadyToRun images + startup warm-up + background token refresh |
| Gate G-1 (AWS) | ~1.2 s first submission after hours idle (service token expired while idle; DB pool pruned) | once-a-minute token keep-alive; `MinPoolSize` held (platform 10, API 5) and opened at startup |
| Gate G-1 (AWS) | first request routed to a task before its warm-up finished | `/health` answers 503 until warm-up completes (≤ 60 s), so the ALB waits |
| Gate G-1 (AWS) | 2,000-row insert was the dominant cost, growing with the table (random UUID v4 keys; a redundant index) | UUID v7 ids; `idx_tasks_file` dropped (covered by `uq_tasks_dest`); binary `COPY` instead of parameter arrays (local p99 435 → 329 ms) |
| Gate G-1 (AWS) | API tasks CPU-starved at 0.25 vCPU (1-min peaks 92 %) | API tasks 0.5 vCPU / 1 GB |
| Gate G-1 (AWS) | db.t4g.micro (1 GB) couldn't cache the grown task tables; insert 150–450 ms | dev RDS moved to db.t4g.small, the class prod already uses; insert back to ~100 ms |
| Smoke after DB restart | every stale pooled connection failed one request after an RDS reboot (500/502) | Npgsql `KeepAlive` 15 s drops dead connections in the background; re-verified: 18/18 requests 200 right after a reboot |
| Terraform plan | ECS task definitions replaced on any upstream change (module-level `depends_on` deferred data sources) | the bootstrap dependency is now on the ECS service resource only; RDS `apply_immediately` in dev |
| Drill (local) | unreachable platform held workers until the transfer timeout | 5 s connect timeout; local edge plays the internal ALB (502 at once) |
| Terraform apply | JWT authorizer created before the issuer's discovery doc was reachable | authorizer waits on the public routes, the stage and a healthy auth service |
| Terraform plan | `count` on values unknown until apply | plan-time booleans / map keys |
