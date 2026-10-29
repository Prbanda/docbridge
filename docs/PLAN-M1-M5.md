# DocBridge M1–M5 build plan (.NET backend)

**Inputs read:** `docs/design/Design Requirements.md`, `DocBridge Blueprint.md`, `DocBridge ADRs.md` (ADR-001…016),
`DocBridge Technical Design.md`, `IMPLEMENTATION_PLAN.md` (amendments AM-1…AM-7), the M0 code in this repo.
**New decisions:** `docs/design/DocBridge ADRs - DotNet.md` (ADR-017 runtime, ADR-018 data access, ADR-019 build).
**Testing docs:** `docs/testing/M1-TESTING.md` … `M5-TESTING.md`, each with: local → deploy to AWS → verify on AWS.

## 1. Is the TDD clear enough to implement?

Yes, with the gaps below. Each gap is something the TDD needs but doesn't specify. Each one gets the
smallest additive answer that leaves the ADRs intact.

| # | Gap in the TDD | Resolution (built in) |
| :-- | :-- | :-- |
| G1 | API Gateway's JWT authorizer must fetch the issuer's discovery doc + JWKS over **public HTTPS**, but the auth service is private (ADR-001). | Token `iss` = the HTTP API's own URL. `GET /.well-known/openid-configuration` and `/.well-known/jwks.json` are public gateway routes to the auth service. Tokens carry `aud: "docbridge"` (the authorizer requires an audience). M1 |
| G2 | Nothing in the TDD creates teams, binders, folders or memberships, so seed data and the M5 "revoked membership" test have no way in. | Platform admin routes behind a service token with scope `platform:admin` (`platform-admin` client, secret in Secrets Manager). `tools/seed` uses them. M1 |
| G3 | Where does the platform store document bytes? | Its own S3 bucket (`…-platform-docs-…`, SSE-KMS, private), separate from staging. M1 |
| G4 | DocBridge API also calls a service route (`members/:userId`, scope `memberships:read`), so it needs a client identity too. | `docbridge-api` service client seeded by auth beside `delivery-worker`. M1 |
| G5 | Tasks store only ids. After a refresh the dashboard can't show destination names. | Optional `label` per destination in `POST /jobs`, stored as `tasks.destination_label`, used for display and failure reasons. M2 |
| G6 | Task visibility timeout = 6× delivery timeout (30 min) makes the wait between retries 30 min. | On a transient failure the worker sets the message's visibility to a backoff (60 s, then 300 s) before throwing. maxReceiveCount stays 3. M2 |
| G7 | S3 can't verify a whole-object SHA-256 on multipart uploads (>100 MB). | Single-part POST: S3 verifies. Multipart: the worker verifies the whole object before delivery and the platform verifies on receipt. Same end-to-end guarantee, one fewer hop for large files. M2 |
| G8 | Sweeper "stale jobs" is fuzzy and risks duplicate fan-out of a healthy backlog. | `jobs.enqueued_at` is set by fan-out. The sweeper republishes only jobs with `enqueued_at IS NULL` older than 2 min. M2 |
| G9 | Multipart needs a completion call; the TDD lists none. | `POST /files/:fileId/multipart/complete`. M2 |
| G10 | Requirements say docker compose runs **everything** (workers too). LocalStack Community has no API Gateway WebSocket. | `workers-local` container: SQS long-poll host that calls the same handler code as the Lambdas, plus a WebSocket emulator exposing the same `@connections` API the AWS SDK posts to. An nginx `edge` container stands in for API Gateway locally. M2/M3 |
| G11 | Who creates the logical DBs and roles in RDS (M0 §2.6 left it open)? | `db-bootstrap` Lambda (in VPC, master secret), invoked by Terraform `aws_lambda_invocation`. Idempotent. No manual step. M1 |
| G12 | Destination teams aren't checked for `docbridge_enabled` at submission. | `POST /jobs` also checks each team against the caller's docbridge-enabled team list (one forwarded call), next to the TDD's membership route. M2 |
| G13 | "Requester shares a team with the submitter" needs the *submitter's* teams, and the WS subscribe Lambda has no user JWT to forward. | Platform service route `GET /users/:userId/teams` (scope `memberships:read`). The rule becomes one cached list intersection (60 s, TDD 3.1). M3/M4 |

**AM-6 superseded (measured):** `idx_tasks_file` duplicated the leading column of `uq_tasks_dest (file_id, …)`, so
any `file_id` lookup is already indexed. It only added write cost to every 2,000-row submission, so migration
`002` drops it. Task, job and file ids are UUID v7 (time-ordered), for the same reason: index locality on insert.

**Environment deviation (dev only):** this AWS account's Lambda concurrency quota is 10, and AWS requires all 10 to
stay unreserved, so TDD 2.4's reserved concurrency of 50 can't be set in dev. Dev caps delivery through the SQS
trigger's maximum concurrency (5). Prod keeps 50 and needs a quota increase first.

## 2. Repository layout after M1

```
DocBridge.slnx, Directory.Build.props, Directory.Packages.props
packages/shared/            DocBridge.Shared: JWT validation, ApiError, Db/Migrator, ServiceTokenClient, PlatformClient
services/auth/              DocBridge.Auth (ASP.NET Core) + migrations/*.sql
services/platform/          DocBridge.Platform + migrations/*.sql
services/docbridge-api/     DocBridge.Api + migrations/*.sql (owns the docbridge DB schema)
workers/common/             DocBridge.Workers.Common: fan-out, delivery, ws, dlq, sweeper logic
workers/fanout|delivery|ws|maintenance/   Lambda entry points (thin)
workers/local-runner/       compose host: SQS pollers + WS emulator (same logic as Lambdas)
tools/db-bootstrap/         Lambda creating DBs/roles in RDS
tools/seed/                 console: demo users, teams, binders, nested folders
tests/DocBridge.UnitTests, tests/DocBridge.IntegrationTests (run against compose or a deployed URL)
web/                        React + Vite + TypeScript SPA
infra/modules/              + alb, ecs-cluster, ecs-service, sqs, lambda, api-gateway, websocket-api, secrets
```

## 3. Tasks (inputs → outputs)

Done means: the output exists, its tests pass, and the milestone testing doc's checks pass.

### M1 — Browse destinations
| Task | Inputs | Outputs |
| :-- | :-- | :-- |
| 1.0 .NET solution + shared lib | ADR-017/018/019, `packages/shared` TS behavior | `DocBridge.Shared` (JWT validator + auth handler, error middleware, migrator, connection builder, service-token client), unit tests |
| 1.1 Auth service | TDD auth contract + schema, ADR-013 | register/login/token/JWKS/openid-config, bcrypt-12, RS256 + `kid`, client seeding, migrations, Dockerfile |
| 1.2 Platform hierarchy + membership | TDD platform schema + read routes | 3 user routes, `members/:userId`, admin routes (G2), migrations |
| 1.3 Platform ingest | TDD `POST /documents` | streamed SHA-256, `onBehalfOf` check, `ON CONFLICT (source_task_id)`, allow-list, platform bucket (G3) |
| 1.4 Public edge | ADR-001, G1 | Terraform: HTTP API + JWT authorizer + VPC Link + internal ALB + ECS cluster/services |
| 1.5 DocBridge API browse proxy | TDD `/destinations/*` | JWT-forwarding proxy, enabled filter, 5 s timeout → 502 |
| 1.6 Web chunk 1 | TDD 1.6 | register/login, in-memory token, lazy tree |
| 1.7 Seed + DB bootstrap | G2, G11 | `tools/seed`, `db-bootstrap` Lambda |
| 1.8 CI for .NET | TDD 0.3 | workflows switched to `dotnet` (lint/format, test, image, ECR, ECS deploy) |

### M2 — Distribute a batch
| Task | Inputs | Outputs |
| :-- | :-- | :-- |
| 2.1 `POST /files`, re-issue (AM-2), multipart complete (G9) | ADR-002/004 | presigned POST with size/type/checksum conditions, multipart plan >100 MB |
| 2.2 `POST /jobs` | TDD 2.2, AM-1, AM-5, AM-6, G5, G12 | one transaction for job + ≤2,000 tasks, one job message, 201 in <500 ms |
| 2.3 Queues + fan-out | ADR-005/008/016 | Terraform queues/DLQs, fan-out Lambda, `enqueued_at` (G8) |
| 2.4 Delivery | TDD 2.4, G6, G7 | claim, HEAD, stream+hash, outcome mapping, job close, reserved concurrency 50 |
| 2.5 DLQ consumers + sweeper | TDD 2.5, AM-3 | `RETRIES_EXHAUSTED`, republish, orphan cleanup |
| 2.6 Web chunk 2 | TDD 2.6 | picker, Web Worker SHA-256, bounded uploads, mapping matrix, submit |

### M3 — Track a job
| Task | Inputs | Outputs |
| :-- | :-- | :-- |
| 3.1 Status endpoints | TDD 3.1 | `GET /jobs` single aggregate query, `/jobs/:id`, `/jobs/:id/tasks` |
| 3.2 WebSocket API | ADR-007, AM-4, AM-7 | WS API + 3 handlers, `ws_connections`, no query strings in access logs |
| 3.3 Push + catch-up | TDD 3.3 | PostToConnection after every status write, 410 cleanup |
| 3.4 Web chunk 3 | TDD 3.4 | job list, virtualized task table, live updates, reconnect + refetch |

### M4 — Retrieve a file
| 4.1 `POST /files/:fileId/download-url` | ADR-002/004, TDD 4.1 | share-a-team check, 2-min presigned GET, 404 outside scope |

### M5 — Hardening
| 5.1 E2E gates | TDD M5 list | automated suite run against compose and deployed dev, results in M5-TESTING |

## 4. What I need from you

Nothing blocks the build. What's already in place: AWS CLI credentials (`pranathi-admin`), the
Terraform state bucket with `backend.hcl` in each env, dev applied, ECR repos, Docker Desktop, and the .NET 10 SDK (installed during this session).

To finish the parts only you can do:
1. **GitHub:** push this branch and open a PR to `main`. CI deploys only from `main`.
   Then set the repo variables `ECS_DEPLOY_ENABLED=true` and `LAMBDA_DEPLOY_ENABLED=true` (M0 §2.4)
   so pushes to `main` roll out services and Lambdas.
2. **Prod:** prod is deployed only by approving the `prod` environment in GitHub Actions, after the M5 gates pass in dev.
   The agent does not apply prod on its own.
3. **Cost:** dev costs about $4–6/day with ECS + ALB + API Gateway. Run `./scripts/aws-down.sh` between sessions.
