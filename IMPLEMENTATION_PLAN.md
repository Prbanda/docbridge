# DocBridge Implementation Plan

**Source documents:** `docs/design/Design Requirements.md`, `docs/design/DocBridge Blueprint.md` (PAS-001), `docs/design/DocBridge ADRs.md` (ADR-001…014), `docs/design/DocBridge Technical Design.md` (TDD)
**Status:** Ratified plan, execution not started
**Rule:** the TDD is the spec. This document sequences the work, defines what "done" means per milestone, and adds the AWS setup runbook and the testing-document convention the TDD does not spell out.

---

## 1. Assumptions locked in before coding

| # | Question | Decision for v1 | Source |
| :-- | :-- | :-- | :-- |
| A1 | Multi-region platform? | One platform deployment. `region` is stored on `teams`/`tasks` but all deliveries route to the single platform URL. Multi-region routing is v2. | Blueprint Release 2, TDD schema note |
| A2 | 30-day retention exactness | S3 lifecycle rule on object age → guarantee is "≥30 days after completion". Exact-to-completion sweep deferred to v2. | TDD Launch plan |
| A3 | Refresh tokens | Not in v1. 15-min access token; expiry returns user to login. | TDD API design |
| A4 | Postgres version | Pin the newest major version **RDS Proxy** supports in our region at M0 (the proxy, required by ADR-011, lags new engine majors — that's the binding constraint, not the Postgres release line). Use the matching official Docker image locally. Since this will be ≥15, use one `UNIQUE NULLS NOT DISTINCT` constraint for the duplicate-destination guard instead of the partial-index pair (TDD's own noted simplification). | TDD data model note, ADR-011 |
| A5 | Environments | `dev` first, end to end. `prod` applied only after M5 gates pass in dev. | TDD Launch plan |

## 2. Stack summary (all fixed by ADRs — no re-litigating)

- **Services (Node.js/TypeScript, ECS Fargate):** `auth`, `platform`, `docbridge-api` — 3 deployables, one internal ALB, one PostgreSQL instance with 3 isolated logical DBs and per-service credentials (ADR-006/009/010).
- **Workers (Lambda):** `fanout` (job queue), `delivery` (task queue), `ws` handlers ($connect/$disconnect/subscribe), DLQ consumers, EventBridge sweeper (ADR-011, TDD 2.5).
- **Edge:** API Gateway HTTP API + JWT authorizer → VPC Link → internal ALB (ADR-001). API Gateway WebSocket API for live status (ADR-007).
- **Files:** S3 staging bucket, SSE-KMS `aws/s3` + Bucket Keys, presigned POST uploads (15 min) / presigned GET downloads (2 min), multipart above 100 MB (ADR-002/003/004).
- **Queues:** SQS job queue + task queue, each with DLQ, `maxReceiveCount 3` (ADR-005/008).
- **Data access:** Drizzle ORM + drizzle-kit migrations, per-service `migrations/` folder, RDS Proxy for all Lambda DB access (ADR-011/014).
- **IaC:** Terraform ≥1.15.8, modules + thin `envs/dev|prod`, S3 state backend with native lockfiles from `bootstrap/` (ADR-012).
- **CI/CD:** GitHub Actions per service with path filters; infra workflow with plan-on-PR / apply-on-merge, manual approval for prod.
- **Local:** Docker Compose — 3 services, Postgres (init script creates 3 DBs + 3 roles), LocalStack (S3, SQS), workers as local processes.
- **Frontend:** React + Vite SPA on S3 + CloudFront.

## 3. Repository layout (to be created in M0)

```
docbridge/
  services/
    auth/           # + migrations/
    platform/       # + migrations/
    docbridge-api/  # + migrations/
  workers/
    fanout/
    delivery/
    ws/
  web/
  packages/shared/  # JWT middleware, error shapes, shared types
  infra/
    modules/        # network, rds, ecs-service, sqs, s3, api-gateway, lambda, ecr
    envs/dev/  envs/prod/
  bootstrap/        # one-time TF state backend script
  docs/testing/     # per-milestone testing documents (see §7)
  docker-compose.yml
  .github/workflows/
```

## 4. Milestone sequence and definitions of done

Order and scope follow the TDD exactly. Each milestone ends with (a) a browser-demoable result, (b) a testing document in `docs/testing/` (see §6).

### M0 — Foundations
**Build:** monorepo scaffold + `packages/shared`; docker-compose with Postgres init (3 DBs/3 roles) + LocalStack; Terraform bootstrap + modules (network, rds + RDS Proxy + Secrets Manager, ecr, s3 staging bucket with SSE-KMS/BPA/TLS-only/30-day lifecycle/7-day multipart abort, SPA bucket + CloudFront); dev env applied; CI/CD skeleton (per-service workflows, infra plan/apply, gitleaks).
**Done when:** `docker compose up` gives a healthy local stack; `terraform apply` in `envs/dev` is green; CI runs on push.

### M1 — Flow 1: Browse destinations
**Build:**
1. Auth service complete (register with bcrypt-12, login with constant-time compare, RS256 + JWKS with `kid`, service-token endpoint per ADR-013, `delivery-worker` client seeded). Deployed: ECS ×2 AZs behind internal ALB, verified from inside the VPC first (ADR-001 rollout note).
2. Platform: schema, 3 user-token read endpoints with membership enforced in the query, service-token membership route, seed data (5 teams, nested folders).
3. Platform ingest `POST /documents` (streamed SHA-256, `onBehalfOf` time-of-use check, `ON CONFLICT (source_task_id) DO NOTHING`, content-type allow-list) — built now, exercised in M2.
4. Public edge: API Gateway HTTP API + JWT authorizer + VPC Link, public routes exempted, CORS, throttling. Verify 401 at the gateway never reaches the ALB.
5. DocBridge API skeleton + `/destinations/*` proxy (JWT forwarded, docbridge-enabled filter, 5 s timeout → 502 mapping).
6. Frontend chunk 1: register/login, token in memory, lazy destination tree (one level per expand).

**Done when:** a registered user logs in in the browser and browses only their teams/binders/folders, <1 s per level.

### M2 — Flow 2: Distribute a batch
**Build:**
1. `POST /files` presigned POST issuance (content-length-range ≤ declared size, exact content-type condition from allow-list, `x-amz-checksum-sha256` condition, 15-min expiry, waves ≤20, multipart plan >100 MB). Plus `POST /files/:fileId/upload-url` re-issuance for expired URLs (AM-2).
2. `POST /jobs` submission transaction: race-safe file claim via conditional `UPDATE … WHERE job_id IS NULL RETURNING` (AM-1), optional `Idempotency-Key` replay (AM-5), membership per distinct team via platform service route, expand mappings, one transaction (job + up to 2,000 task rows), publish one job message, return 201 <500 ms. Publish-after-commit failure → still 201; sweeper covers it. Migrations include `idx_tasks_file` (AM-6).
3. Queues via Terraform: job/task queues + DLQs, `maxReceiveCount 3`, visibility timeouts (job 2 min, task 6× delivery timeout).
4. Fan-out Lambda: `SendMessageBatch` of `{taskId}` only; idempotent on redelivery.
5. Delivery Lambda: conditional claim `UPDATE … WHERE status IN ('pending','in_progress') RETURNING *`; HEAD staged object (missing → permanent `FILE_NOT_UPLOADED`); stream S3→SHA-256→platform in one pass; cached service JWT; outcome mapping (201/200→completed, 403→`NOT_AUTHORIZED_AT_DELIVERY`, 422→`CHECKSUM_MISMATCH`, 5xx/timeout→throw for SQS retry); race-safe `jobs.completed_at` close; RDS Proxy; reserved concurrency 50.
6. DLQ consumers (`RETRIES_EXHAUSTED`, user-visible) + 15-min EventBridge sweeper republishing stale jobs; sweeper also purges never-submitted `files` rows past retention (AM-3).
7. Frontend chunk 2: picker (≤100, allow-list), SHA-256 in a Web Worker, bounded-concurrency upload queue (4–6) with progress, mapping matrix with live task count, submit gated on uploads complete.

**Done when:** 100 files × destinations submit in <500 ms, documents land in the platform, and the pipeline survives a platform kill-and-recover with an empty DLQ.

### M3 — Flow 3: Track a job
**Build:** status endpoints (`GET /jobs` single aggregate query, `GET /jobs/:id`, `GET /jobs/:id/tasks` paginated/filterable; cross-member view requires shared team, cached 60 s); WebSocket API + `$connect`/`subscribe`/`$disconnect` Lambdas + `ws_connections` registry (sweeper purges rows older than 2 h, AM-4; access logs exclude query strings, AM-7); push from delivery worker (PostToConnection, 410→delete row, failures swallowed); client catch-up (refetch once on subscribe/reconnect); frontend chunk 3 (job list with aggregate chips, virtualized task table, failure reasons inline, WS reconnect; download button UI stubbed).

**Done when:** task rows and counts flip live on the dashboard during a run and survive a mid-run page refresh.

### M4 — Flow 4: Retrieve a file
**Build:** `POST /files/:fileId/download-url` — requester must share a team with the job submitter; 2-min single-object presigned GET; 404 (not 403) outside scope.

**Done when:** the download button returns the file for teammates and 404s for everyone else.

### M5 — Hardening + E2E verification (gates prod)
Run against deployed dev: p99 <500 ms on a 2,000-task submission; partial-failure isolation (1 failed / 1,999 completed); platform kill mid-run → backlog drains, DLQ empty; `FILE_NOT_UPLOADED` surfaced; security pass (gateway 401s, cross-team 404s, expired URLs fail, gitleaks clean); manual UX checks (throttled upload, multipart within-session resume, WS reconnect).

**Done when:** all gates green in dev → `terraform apply` to prod via approval gate.

## 5. TDD amendments (design-review findings)

Refinements agreed on top of the TDD. None changes an ADR; each is folded into the milestone step where it's built (referenced below as AM-1…AM-7).

| # | Finding | Amendment | Built in |
| :-- | :-- | :-- | :-- |
| AM-1 | TDD 2.2 says "validate fileId is unclaimed" then inserts — two concurrent submissions (double-click, two tabs) can both pass validation and claim the same files. | Claim files with a conditional update inside the submission transaction: `UPDATE files SET job_id = $job WHERE id = ANY($ids) AND job_id IS NULL RETURNING id`; abort with 409 if the returned count is short. Same pattern as the delivery worker's task claim. | M2 step 2 |
| AM-2 | Frontend contract requires "re-request URLs on expiry", but the only issuance endpoint (`POST /files`) creates new file rows — re-requesting would orphan the originals. | Add `POST /files/:fileId/upload-url`: re-issues a presigned POST (or multipart plan) for an existing file. Owner-only, allowed only while `job_id IS NULL`. | M2 step 1 |
| AM-3 | Files uploaded but never submitted keep their `files` rows forever; S3 lifecycle deletes the bytes after 30 days, leaving dangling rows. | The EventBridge sweeper also deletes `files` rows where `job_id IS NULL` and `created_at` is past the retention window. | M2 step 6 |
| AM-4 | API Gateway `$disconnect` is best-effort; `ws_connections` rows accumulate for connections that never receive a push (410 cleanup only covers pushed-to connections). | Sweeper purges `ws_connections` rows older than 2 h (the WebSocket API's max connection duration). | M3 step 2 |
| AM-5 | A `POST /jobs` retry after a post-commit network timeout fails validation ("files already claimed") even though the job exists — loud but confusing. | Accept an optional `Idempotency-Key` header; unique-indexed on `jobs`, a replay returns the existing job (200). | M2 step 2 |
| AM-6 | Postgres doesn't auto-index FK columns; the download path resolves file → job → submitter. | Add `CREATE INDEX idx_tasks_file ON tasks(file_id)`. | M2 migrations |
| AM-7 | WS auth passes the JWT as a query param (pragmatic per TDD), but query strings can land in gateway access logs. | Exclude query strings from the WebSocket API access-log format. Documented v2 upgrade: short-lived one-time connection ticket. | M3 step 2 |

## 6. AWS setup runbook (your side)

The only manual steps are credentials and one bootstrap script; everything else is Terraform + CI (per the operability requirement). Full copy-paste detail lands in `docs/testing/M0-TESTING.md`; this is the shape:

1. **Account & IAM (once).** An AWS account with billing alerts; create an IAM identity for yourself (console) and a deploy role/user for GitHub Actions — we'll use GitHub OIDC federation so no long-lived AWS keys live in GitHub secrets.
2. **Local tooling (once).** `aws` CLI v2, `terraform` ≥1.15.8, `docker`, `node` ≥22. `aws configure` (or SSO) against the account. **Region: `us-east-1` (decided).**
3. **Bootstrap state backend (once).** Run `./bootstrap/create-state-backend.sh` → versioned S3 state bucket with S3-native lockfiles (the single manual resource per ADR-012; no DynamoDB).
4. **GitHub repo wiring (once).** Push the repo, add the OIDC role ARN + a couple of non-secret config values as GitHub Actions variables; create the `prod` environment with required manual approval.
5. **Deploy dev.** `cd infra/envs/dev && terraform init && terraform apply` (or merge to main and let the infra workflow do it). First apply takes ~20–30 min (VPC, NAT, RDS, proxy, CloudFront).
6. **Per-milestone deploys.** Push to main → path-filtered workflows build/push images and update ECS/Lambda. No console writes ever; console is for reading logs/metrics.
7. **Prod.** Only after M5 gates: approve the `prod` environment run. Same modules, prod variables.
8. **Cost control (dev).** Single NAT gateway, smallest RDS instance class (`db.t4g.micro`) + proxy, Fargate 0.25 vCPU tasks, CloudFront price class 100. Expect roughly $3–5/day for dev while it's up. **Between practice sessions run `./scripts/aws-down.sh`** — it destroys everything billable (dev VPC/NAT/RDS/S3/CloudFront; buckets have `force_destroy` in dev so teardown always succeeds) and keeps only the state backend (pennies/month) and ECR repos. `./scripts/aws-up.sh` recreates dev in ~20–30 min. Dev data is disposable by design; seed scripts repopulate it.

## 7. Testing-document convention (per your request)

At the end of every milestone I will write **one document per milestone**: `docs/testing/M{n}-TESTING.md`, each with the same three sections:

1. **Local verification** — exact commands to bring up docker-compose/LocalStack, seed data, and step-by-step checks (curl + browser) proving the milestone's definition of done locally.
2. **Deploy to AWS (dev)** — what to push/apply, in what order (services + ALB verified from inside the VPC before gateway routes, per ADR-001), and how to confirm the deploy landed.
3. **Verify in production-like environment** — the same functional checks against the deployed dev URL (and later prod), plus the milestone's specific gates (e.g. M2: kill-and-recover drill; M5: full success-metric suite).

M0's document additionally contains the full AWS account setup runbook from §6 with copy-paste commands.

## 8. Standing test strategy (from the TDD, applied continuously)

- **Unit (every CI run):** token issuance/validation, presigned policy construction, task claim logic, aggregate status derivation, transient-vs-permanent failure classification.
- **Integration (compose + LocalStack, nightly + on worker changes):** submit → fan-out → deliver; double-delivery of one task message; checksum mismatch; DLQ path marks tasks failed.
- **E2E (deployed dev):** the M5 list, gated before any prod apply.

### 8.1 Agent self-validation gates (run before every handoff to you)

Before any milestone is handed over, I run these myself and report results — nothing reaches you unverified:

| Gate | What runs | Proves |
| :-- | :-- | :-- |
| SV-1 Static | `npm run typecheck` + `npm run lint` across all workspaces; `terraform fmt -check` + `terraform init -backend=false && terraform validate` on every module and both envs; workflow YAML lint; gitleaks scan (docker) | Code compiles, style holds, Terraform is syntactically and referentially valid, no secrets committed |
| SV-2 Unit | `npm test` in every workspace (vitest) | The TDD's unit list: token issuance/validation, presigned policies, claim logic, status derivation, failure classification |
| SV-3 Local integration | `docker compose up --build` from clean state, then `scripts/smoke-local.sh`: service health + DB-readiness per service credential, LocalStack bucket/queues exist. From M2 onward this grows into the full local pipeline: presign → upload → submit → fan-out → deliver → status flip, plus double-delivery and checksum-mismatch cases against LocalStack | The demoable flow works end to end locally, including Dockerfiles and per-service DB isolation |
| SV-4 Boundary honesty | Explicit list, per milestone, of what **cannot** be self-validated (real IAM, VPC Link, RDS Proxy, CloudFront, JWT authorizer behavior) | These land in the milestone's `M{n}-TESTING.md` §2–3 as your verification steps — the split between "verified by me" and "verify on AWS" is always written down |

CI mirrors SV-1/SV-2 on every push, so the gates keep holding after handoff.

## 9. Execution status

- **M0 — code complete, AWS-verified 2026-07-24.** Monorepo + shared package + 3 service skeletons + 3 worker stubs; docker-compose (Postgres 3 DBs/3 roles, LocalStack bucket + queues) + smoke script; Terraform bootstrap + modules (network, rds+proxy, ecr, s3+CloudFront) + `envs/{global,dev,prod}`; CI workflows (per-service, workers, infra plan/apply with prod gate, gitleaks). Self-validated: typecheck, lint, 24 unit tests, `terraform validate` ×3 envs, gitleaks clean, worker bundles build. **Live AWS verification:** state backend bootstrapped, global (ECR) + dev applied cleanly (49 resources, Postgres 17.9 + proxy available — A4 confirmed), all §3 security checks passed (RDS private/encrypted, SSE-KMS + BPA + lifecycle, TLS-only enforced), then destroyed via teardown path (0 billable resources remain). Two real-AWS fixes folded back into the modules: KMS `ViaService` condition instead of the aws/secretsmanager alias lookup (alias doesn't exist in fresh accounts), and `apply_method = "pending-reboot"` on `rds.force_ssl` (perma-diff fix).
- **Still pending for M0 sign-off:** local docker compose smoke (Docker not yet installed), GitHub repo push + CI green (repo not yet on GitHub, OIDC role not yet created).
- **Next:** M1 Step 1.1 (auth service, complete, deployed) once M0 is signed off.
