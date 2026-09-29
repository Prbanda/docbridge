# Technical Design for PAS-001: DocBridge

**Status:** Draft   
**Blueprint:** [DocBridge Blueprint](https://docs.google.com/document/d/1Hbd7QWzfPQv-cA7dJdZzGvJ5O_cgGA9MqcKprZA4GoQ/edit?tab=t.f1wp5ygws6xm#heading=h.yr0v5w8tqfi8)  
**ADRs:** [DocBridge ADRs](https://docs.google.com/document/d/1MYtGquZnQmUzaqCRCCNzYgm64InB8YUcwBmnoMxvw7s/edit?tab=t.0)  
**Collaborators:** [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

**Rules for implementation:**

- **IaC:** All resources are created by Terraform. Console is used for reading, not writing.  
- **CI/CD:** Every service gets a GitHub Actions workflow when it's created.  
- **Local first:** Every part runs locally via Docker Compose (LocalStack for S3/SQS/KMS)  
- **Secrets:** Nothing sensitive in code. Secrets Manager \+ env variables.  
- **Decisions:** When a part hits a decision with real competing options, stop and write an ADR using the template before implementing. Decisions with one obvious answer get a one-line note in this doc, not an ADR.

## Build philosophy

Milestones follow the Blueprint's 4 user flows. Each of M1–M4 ends with something a user can test in the browser.

Within a milestone: when a flow touches a service, build that service close to its full project scope, not the thin slice the flow strictly needs. Reopening a service later for one endpoint costs more than building it now. 

## Estimate compression logic (AI-assisted implementation)

**Estimates assume AI-assisted implementation** (Claude Code, Cursor, Codex, etc): AI writes most of the boilerplate, Terraform, and tests. The lead engineer will review and verify everything.

Compression applied per work type:

- **Boilerplate** (CRUD endpoints, Terraform modules, compose files, CI YAML, UI scaffolding): compressed 50–60%  
- **Concurrency-sensitive logic** (submission transaction, worker claim/retry, idempotency): compressed 30–40% only. The bottleneck is reasoning about interleavings and testing them, not code volume.  
- **Infra wiring and first-time cloud debugging** (VPC endpoints, IAM policies, ALB health checks, API Gateway \+ VPC Link): compressed minimally. Bounded by AWS propagation time and console debugging, not typing speed.

## Milestones

| Milestone | Flow | Estimate | Demoable at the end |
| :---- | :---- | :---- | :---- |
| M0 | Foundations (no flow) | 4.5 d | Infra live, pipelines green, local env runs |
| M1 | Flow 1: Browse destinations | 9 d | Log in, browse the team/binder/folder tree in the browser |
| M2 | Flow 2: Distribute a batch | 9.5 d | Select files, watch uploads, map destinations, submit, get confirmation; documents land in the platform |
| M3 | Flow 3: Track a job | 6 d | Watch a job's tasks flip live on the dashboard |
| M4 | Flow 4: Retrieve a file | 1 d | Download button returns the file |
| M5 | Hardening \+ E2E verification | 2 d | Success metrics proven on the deployed system |
| **Total** |  | **32 d** |  |

Deploy order within milestones follows ADR-001's rollout note: services \+ internal ALB verified from inside the VPC before the gateway routes to them.

## Repository and environment structure

```
docbridge/
  services/
    auth/           # includes services/auth/migrations/
    platform/       # includes services/platform/migrations/
    docbridge-api/  # includes services/docbridge-api/migrations/
  workers/
    fanout/
    delivery/
    ws/
  web/
  packages/shared/  # JWT validation middleware, error shapes, shared types
  infra/
    modules/        # reusable Terraform: network, rds, ecs-service, sqs, s3, api-gateway, lambda
    envs/
      dev/          # thin config calling modules with dev variables
      prod/         # thin config calling modules with prod variables
  bootstrap/        # one-time state backend creation (ADR-012)
  docker-compose.yml
  .github/workflows/
```

Two rules made explicit: `infra/envs/*` never duplicates resource blocks, only calls `infra/modules/*` with different variables. Each service owns its own migrations folder inside its directory, consistent with each service owning its logical database (ADR-009). No central migrations folder.

## Database Design

3 isolated logical databases, one PostgreSQL instance, per-service credentials (ADR-009, ADR-010). No cross-database foreign keys; cross-service references are plain UUIDs validated through service APIs.

### auth database

```sql
users (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email         citext UNIQUE NOT NULL,
  password_hash text NOT NULL,
  name          text NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now()
);
service_clients (
  id                 uuid PRIMARY KEY,
  client_id          text UNIQUE NOT NULL, --e.g. 'delivery-worker'
  client_secret_hash text NOT NULL,
  scopes             text[] NOT NULL,      --['documents:ingest']
  created_at         timestamptz NOT NULL
);
```

Note on DocBridge access control: The users table above is authentication only, it proves who someone is, nothing more. Whether a user can use DocBridge at all is not modeled as a user-level flag. It's derived from two facts, both in the platform database: team\_members (is this user on this team) and teams.docbridge\_enabled (does this team have DocBridge turned on). A user's DocBridge access is the intersection of those two. This matches the Blueprint's stated authorization model: team membership is the entire authorization model for v1, no per-user overrides.

### platform database

```sql
teams (
  id                 uuid PRIMARY KEY,
  name               text NOT NULL,
  region             text NOT NULL,     -- modeled now for v2
  docbridge_enabled  boolean NOT NULL DEFAULT true,
  created_at         timestamptz NOT NULL
);
team_members (
  team_id  uuid REFERENCES teams(id),
  user_id  uuid NOT NULL,        -- auth user id, no cross-db FK
  added_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (team_id, user_id)
);
CREATE INDEX idx_team_members_user ON team_members(user_id);
binders (
  id         uuid PRIMARY KEY,
  team_id    uuid NOT NULL REFERENCES teams(id),
  name       text NOT NULL,
  created_at timestamptz NOT NULL
);
CREATE INDEX idx_binders_team ON binders(team_id);
folders (
  id               uuid PRIMARY KEY,
  binder_id        uuid NOT NULL REFERENCES binders(id),
  parent_folder_id uuid REFERENCES folders(id),  -- NULL = binder root
  name             text NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_folders_parent ON folders(binder_id, parent_folder_id);
documents (
  id                  uuid PRIMARY KEY,
  binder_id           uuid NOT NULL REFERENCES binders(id),
  folder_id           uuid REFERENCES folders(id),
  name                text NOT NULL,
  size_bytes          bigint NOT NULL,
  content_type        text NOT NULL,
  checksum_sha256     text NOT NULL,
  source_task_id      uuid UNIQUE,   -- idempotency key for ingest
  uploaded_by_user_id uuid NOT NULL,        -- the onBehalfOf user
  created_at          timestamptz NOT NULL
);
```

`source_task_id UNIQUE` ensures the ingest idempotency guarantee: same task delivered twice inserts once (`ON CONFLICT DO NOTHING`, return the existing document).

Hierarchy access pattern is expand-one-level (indexed adjacency list), matching ADR-010. No recursive CTE on the hot path.

### docbridge database

files is defined before jobs below for readability, but files.job\_id references jobs(id). The actual migration creates jobs first, or creates both tables then adds the FK as a 2nd step.

```sql
files (
  id              uuid PRIMARY KEY,
  owner_user_id   uuid NOT NULL,
  job_id          uuid REFERENCES jobs(id), -- NULL until submitted
  original_name   text NOT NULL,
  size_bytes      bigint NOT NULL,
  content_type    text NOT NULL,
  checksum_sha256 text NOT NULL,      -- client-declared, verified by S3 + worker + platform
  s3_key          text NOT NULL,      --staging/{owner_user_id}/{file_id}
  created_at      timestamptz NOT NULL
);

jobs (
  id                   uuid PRIMARY KEY,
  submitted_by_user_id uuid NOT NULL,
  task_count           int NOT NULL,
  completed_at         timestamptz,           -- set when last task resolves; drives 30-day retention
  created_at           timestamptz NOT NULL
);

CREATE INDEX idx_jobs_user ON jobs(submitted_by_user_id, created_at DESC);

tasks (
  id                   uuid PRIMARY KEY,
  job_id               uuid NOT NULL REFERENCES jobs(id),
  file_id              uuid NOT NULL REFERENCES files(id),
  team_id              uuid NOT NULL,
  binder_id            uuid NOT NULL,
  folder_id            uuid,               -- NULL = binder root
  region               text NOT NULL,
  status               text NOT NULL DEFAULT 'pending'
                       CHECK (status IN ('pending','in_progress','completed','failed')),
  attempt_count        int NOT NULL DEFAULT 0,
  failure_reason       text,
  platform_document_id uuid,
  created_at           timestamptz NOT NULL,
  updated_at           timestamptz NOT NULL
);

-- Duplicate-destination guard. A plain UNIQUE (file_id, team_id, binder_id, folder_id)
-- silently fails for binder-root destinations because NULL never equals NULL in a
-- standard unique constraint. Fix: pair of unique indexes, one partial for the NULL case.
CREATE UNIQUE INDEX uq_tasks_dest
  ON tasks(file_id, team_id, binder_id, folder_id) WHERE folder_id IS NOT NULL;

CREATE UNIQUE INDEX uq_tasks_dest_root
  ON tasks(file_id, team_id, binder_id) WHERE folder_id IS NULL;

CREATE INDEX idx_tasks_job_status ON tasks(job_id, status);

ws_connections (
  connection_id text PRIMARY KEY,     -- API Gateway connection id
  user_id       uuid NOT NULL,
  job_id        uuid,                 -- currently subscribed job
  connected_at  timestamptz NOT NULL
);

CREATE INDEX idx_ws_job ON ws_connections(job_id);
```

API Gateway's WebSocket API manages the raw connection only (accept, keep-alive, connection ID, PostToConnection). It has no built-in pub/sub and does not track which user is subscribed to which job. Without this table, the only alternatives are broadcasting every update to every open connection, which doesn't scale and leaks job data across teams, or building this exact state tracking some other way. Keep this table.

Partial indexes were chosen over a sentinel "binder root" UUID because a magic UUID leaks into application code, platform API calls, and every join against `folders`, while the index pair is invisible to the app. (If we pin PostgreSQL ≥15, `UNIQUE NULLS NOT DISTINCT` collapses both into one constraint; the partial-index version works on any version, so it's the default.)

Job status is **derived on read** (ADR-010). Aggregate mapping: any `failed` and nothing pending/in\_progress → `completed_with_errors`; all `completed` → `completed`; otherwise `in_progress` (or `pending` if nothing started).

**Implementation note for `GET /jobs`:** the paginated job list computes counts in **one** aggregate query across the whole page, never a per-job query in a loop:

```sql
SELECT j.id, j.created_at, j.task_count,
       count(*) FILTER (WHERE t.status = 'pending')     AS pending,
       count(*) FILTER (WHERE t.status = 'in_progress') AS in_progress,
       count(*) FILTER (WHERE t.status = 'completed')   AS completed,
       count(*) FILTER (WHERE t.status = 'failed')      AS failed
FROM jobs j LEFT JOIN tasks t ON t.job_id = j.id
WHERE j.id = ANY($pageOfJobIds)
GROUP BY j.id;
```

**Multipart resume scope note:** resuming a multipart upload across sessions (close the tab, come back days later) is out of v1 scope. Within-session resume works by holding the upload ID client-side. If cross-session resume is ever needed, the v2 upgrade path is a `multipart_upload_id` column on `files`.

## API Design & Contracts

All JSON. All routes require a JWT except `POST /auth/register`, `POST /auth/login`, `POST /auth/token`, and `GET /.well-known/jwks.json` (ADR-001). Error shape everywhere: `{ "error": { "code": "string", "message": "string" } }`.

### Auth service

| Method \+ path | Request | Response |
| :---- | :---- | :---- |
| POST /auth/register | `{email, password, name}` | 201 `{id, email, name}` |
| POST /auth/login | `{email, password}` | 200 `{accessToken, expiresIn: 900}` (15-min user JWT) |
| POST /auth/token | `{clientId, clientSecret, scope}` | 200 `{accessToken, expiresIn: 900}` (service JWT, ADR-013) |
| GET /.well-known/jwks.json | – | 200 JWKS (public keys, both services validate against this) |

User JWT claims: `sub` (user id), `email`, `exp`, `iat`. Service JWT claims: `sub` (client id), `scope`, `token_use: 'service'`.

**Refresh tokens: deferred to v2, documented here so the path is known.** v1 ships the single 15-minute access token; on expiry the SPA sends the user back to login. The v2 upgrade is one `refresh_tokens` table (`token_hash`, `user_id`, `expires_at`, `revoked boolean`), one `POST /auth/refresh` endpoint, and a client-side interceptor that catches a 401, refreshes once, and retries. This is purely additive: the JWT authorizer at the edge and every service's auth middleware validate access tokens only and are untouched, so deferring costs no rework. Roughly 1 day when built. Token rotation and reuse detection are excluded from both v1 and this v2 note; that's a further upgrade if the threat model ever calls for it.

### Document platform

User-token routes (JWT forwarded by DocBridge API or gateway):

| Method \+ path | Response |
| :---- | :---- |
| GET /teams?docbridgeEnabled=true | Teams the token subject belongs to: `[{id, name, region}]` |
| GET /teams/:teamId/binders | 403 if not a member. `[{id, name}]` |
| GET /binders/:binderId/contents?folderId= | One level: `{folders: [...], documents: [...]}` (`contents` because the response mixes two types; `children` implied one) |

Service-token routes (scope-checked):

| Method \+ path | Request | Response |
| :---- | :---- | :---- |
| GET /teams/:teamId/members/:userId | – | 200 `{addedAt}` if member, 404 if not. The status code carries the answer; no `{isMember}` body flag. (scope `memberships:read`) |
| POST /documents | multipart: metadata part `{taskId, binderId, folderId?, name, contentType, checksumSha256, onBehalfOf}` \+ file bytes | 201 `{documentId}` / 200 if `taskId` already ingested / 403 if `onBehalfOf` user not a member at time of use / 422 `CHECKSUM_MISMATCH` (scope `documents:ingest`, ADR-013) |

Platform recomputes SHA-256 of received bytes and rejects on mismatch. End of the integrity chain: client declares → S3 verifies on write → worker verifies before send → platform verifies on receipt.

### DocBridge API

| Method \+ path | Request | Response |
| :---- | :---- | :---- |
| GET /destinations/teams | – | Proxied from platform, filtered to docbridge-enabled |
| GET /destinations/teams/:id/binders | – | Proxied, authorized |
| GET /destinations/binders/:id/contents?folderId= | – | Proxied, one level (lazy tree) |
| POST /files | `{files: [{name, sizeBytes, contentType, sha256}]}` max 100 | 201 `[{fileId, upload: presignedPostFieldsOrMultipartPlan}]` — creates `files` resources; named after the resource created, matching the pattern everywhere else |
| POST /jobs | `{mappings: [{fileId, destinations: [{teamId, binderId, folderId?}]}]}` | 201 `{jobId, taskCount}` in \<500 ms (ADR-008) |
| GET /jobs?page=\&limit= | – | `[{jobId, createdAt, taskCount, counts, aggregateStatus}]` (single aggregate query, see data model note) |
| GET /jobs/:id | – | Job \+ counts |
| GET /jobs/:id/tasks?status=\&page=\&limit=100 | – | Paginated tasks with `failureReason` (2,000 tasks never in one response) |
| POST /files/:fileId/download-url | – | `{url, expiresIn: 120}` — stays POST deliberately: non-idempotent, mints a short-lived credential (ADR-002/004) |

Upload URL issuance rules (ADR-002/004): presigned **POST** with `content-length-range` capped at declared size (hard ceiling 1 GB), exact `Content-Type` condition (allow-list: pdf, docx, xlsx, png, jpg), `x-amz-checksum-sha256` condition so S3 verifies on write. URLs expire in 15 minutes; the client requests them in waves of ≤20. Files over 100 MB get a multipart plan with individually signed part URLs (within-session resume only, per scope note above).

### WebSocket API (API Gateway WebSocket, ADR-007)

| Route | Behavior |
| :---- | :---- |
| $connect | JWT passed as query param (WS handshake can't set headers reliably); Lambda validates against JWKS, inserts `ws_connections` row. Reject → 401, no connection. |
| subscribe | `{action: 'subscribe', jobId}`; Lambda verifies the caller may view the job (same rule as GET /jobs/:id), sets `job_id` on the connection row |
| $disconnect | Delete the connection row |

Push message shape: `{type: 'task_update', jobId, taskId, status, failureReason?, counts: {...}}`. Counts ride along so the client updates the aggregate without refetching.

## Milestones

### M0: Foundations (4.5 days)

**Step 0.1: Monorepo, local environment, shared package (1 day)**

- Repo layout as specified above. Shared TypeScript config, ESLint, `packages/shared` with the JWT validation middleware (one implementation, both services import it) and common error shapes.  
- `docker-compose.yml`: 3 service containers, Postgres with an init script creating 3 logical databases \+ 3 roles, LocalStack (S3, SQS), workers runnable as local processes. Meets the "runnable locally" requirement verbatim.

**Step 0.2: Terraform foundation (2.5 days)**

- `bootstrap/` script creates the state backend (S3 \+ DynamoDB lock), the single manual step (ADR-012).  
- `infra/modules/`: network (VPC, 2 AZs, private subnets for everything, NAT), rds (single Postgres instance, RDS Proxy, Secrets Manager secrets per logical DB role), ecr, s3 (staging bucket with SSE-KMS `aws/s3` \+ Bucket Keys per ADR-003, Block Public Access, TLS-only policy, 30-day lifecycle expiry, 7-day multipart-abort cleanup; plus SPA hosting bucket \+ CloudFront).  
- `infra/envs/dev` and `infra/envs/prod` call the modules with per-environment variables. No duplicated resource blocks.  
- Logical databases and per-service roles created by a migration bootstrap job, not by hand (ADR-009). Each service's migrations run from its own `migrations/` folder.

**Step 0.3: CI/CD skeleton, GitHub Actions (1 day)**

- Per-service workflow with path filters (`services/auth/**` only triggers auth): lint → test → docker build → push to ECR tagged with git SHA → deploy (task definition update). Worker workflow: esbuild bundle → update function code.  
- Infra workflow: `terraform plan` as a PR comment, apply on merge, manual approval environment for prod.  
- Rollback \= re-run deploy with the previous SHA.

### M1: Flow 1, Browse destinations (9 days)

Demo at the end: a member registers, logs in, and browses the team → binder → folder tree in the browser, filtered to their memberships.

**Step 1.1: Auth service, complete, deployed (2.5 days)**

- Full scope in one pass per the build philosophy: registration (bcrypt cost 12, generic duplicate-email error), login (constant-time compare), RS256 signing with keypair in Secrets Manager, JWKS endpoint with `kid`, **and** the service token endpoint (client credentials, scope validation, `token_use: 'service'`, ADR-013) even though nothing calls it until M2. Coming back to auth later for one endpoint costs more than building it now.  
- Seed the `delivery-worker` client via migration; secret in Secrets Manager.  
- Deploy: ECS Fargate (2 tasks across AZs), internal ALB, health check, verified from inside the VPC (ADR-001 rollout note).

**Step 1.2: Platform hierarchy, membership route, deployed (1 day)**

- Schema, the three user-token read endpoints with membership enforcement in the query, `GET /teams/:teamId/members/:userId` for service callers, demo seed data (5 teams across regions, nested folders) for the ≤50-teams \<1 s latency test.  
- Deploy: second ECS service \+ ALB rule.

**Step 1.3: Platform ingest endpoint (1.5 days)**

- Built now, in M1, because it completes the platform's project scope; first exercised end to end by the delivery worker in M2. Covered by integration tests until then.  
- `POST /documents`: multipart stream, SHA-256 recomputed while streaming to the platform's own store, `onBehalfOf` membership checked at time of use (ADR-013), `ON CONFLICT (source_task_id) DO NOTHING` with the existing document returned on conflict, content-type allow-list (no executables).

**Step 1.4: Public edge (1.5 days)**

- API Gateway HTTP API, native JWT authorizer against the auth JWKS, VPC Link to the internal ALB, routes for all three services, public routes exempted, CORS for the SPA origin, default throttling.  
- Verify: unauthenticated request returns 401 at the gateway and never reaches the ALB.  
- Minimal compression on this step: bounded by AWS propagation and debugging, not code.

**Step 1.5: DocBridge API skeleton \+ browsing proxy, deployed (1 day)**

- Service, docbridge logical DB, migrations. `/destinations/*` proxy the platform with the user's JWT forwarded (ADR-006: membership enforcement lives in the platform; DocBridge adds the docbridge-enabled filter). 5 s timeout, platform errors mapped to 502\.

**Step 1.6: Frontend chunk 1, browse destinations UI (1.5 days)**

- React \+ Vite SPA on the S3 \+ CloudFront hosting from M0. Register/login screens, token in memory (15-min expiry sends the user back to login in v1, see refresh token note).  
- Lazy destination tree: teams → binders → one folder level per expand, matching the platform's one-level API so the \<1 s target is per-level. Multi-select deferred to chunk 2 where mapping needs it.

### M2: Flow 2, Distribute a batch (9.5 days)

Demo at the end: a member selects up to 100 files, watches parallel uploads with progress, maps files to destinations, submits, and gets a confirmation with the task count. Deliveries land in the platform (verified via the platform API), including through kill-and-recover of the platform.

The async pipeline steps below carry the lightest compression in the plan, deliberately.

**Step 2.1: Presigned uploads, POST /files (1.5 days)**

- Insert `files` rows, presigned POST per file with the conditions in the contract section, multipart plan for \>100 MB. Bucket itself already exists from M0.  
- Client contract: browser computes SHA-256 before requesting URLs.

**Step 2.2: Job submission transaction (1.5 days)**

- `POST /jobs`: validate every `fileId` belongs to the caller and is unclaimed; validate destinations via `GET /teams/:teamId/members/:userId` (one call per distinct team, 200/404, cached per request); expand mappings to task rows; one transaction inserting job \+ up to 2,000 tasks (multi-row insert); publish **one** job message; return. Constant work per request is how the 500 ms p99 holds (ADR-008).  
- SQS publish fails after commit → still return 201; the sweeper (2.5) republishes. Never fail a submission the DB accepted, never lose it silently.

**Step 2.3: Queues \+ fan-out worker (1 day)**

- Terraform (from `infra/modules/sqs`): job queue \+ DLQ, task queue \+ DLQ, `maxReceiveCount 3` (ADR-005), visibility timeouts: job 2 min, task 6× delivery timeout.  
- Fan-out Lambda: load task IDs, `SendMessageBatch` in 10s, body `{taskId}` only (no tokens in queues). Idempotent by construction: re-delivery re-sends, per-task idempotency absorbs it (ADR-008).

**Step 2.4: Delivery worker (2.5 days)**

- Built together with fan-out in this milestone; they're one pipeline, not two features.  
- Claim: `UPDATE tasks SET status='in_progress', attempt_count=attempt_count+1 WHERE id=$1 AND status IN ('pending','in_progress') RETURNING *`; terminal status → ack and exit.  
- HEAD the S3 object; missing → permanent `FILE_NOT_UPLOADED` (blueprint risk \#3). Stream S3 GET → SHA-256 on the fly → platform `POST /documents`, single pass, no buffering 1 GB in memory (ADR-011).  
- Service JWT via client credentials, cached across warm invocations, refreshed on expiry (ADR-013).  
- Outcomes: 201/200 → `completed` \+ document id; 403 → permanent `NOT_AUTHORIZED_AT_DELIVERY` (membership revoked between submit and delivery, surfaced not retried); 422 → permanent `CHECKSUM_MISMATCH`; 5xx/timeout → throw, SQS retries via visibility timeout. Permanent failures ack; transient failures throw. `failure_reason` carries file name, destination, cause.  
- On any terminal transition, set `jobs.completed_at` if no unresolved tasks remain (conditional UPDATE with NOT EXISTS subquery, race-safe).  
- All DB access through RDS Proxy (ADR-011).  
- Reserved concurrency on the delivery Lambda set to 50, starting value. Without a cap, a 2,000-task burst can spin up enough concurrent invocations to exhaust the RDS Proxy connection pool. Tunable in Terraform.

**Step 2.5: DLQ consumers \+ safety sweeper (1 day)**

- DLQ consumer for both DLQs: mark the task (or a job's untouched tasks) `failed` with `RETRIES_EXHAUSTED` \+ last error. This turns "no silent loss" from a queue guarantee into a user-visible status.  
- EventBridge sweeper (15 min): republish job messages for stale jobs with no task progress, covering the commit-then-publish-failed window from 2.2. Idempotent like fan-out.

**Step 2.6: Frontend chunk 2, select, upload, map, submit (2 days)**

- File picker (100 max, allow-list mirrored client-side), SHA-256 in a Web Worker, upload queue with bounded concurrency (4–6), per-file progress, URL waves of 20 with re-request on expiry, multipart with within-session resume.  
- Mapping UI: files × destinations matrix with select-all shortcuts, live task-count indicator ("100 files × 20 destinations \= 2,000 deliveries").  
- Submit blocked until all mapped uploads complete client-side; the server still verifies before delivery (defense in depth).

### M3: Flow 3, Track a job (6 days)

Demo at the end: submit a job, watch the dashboard's task counts and rows flip live, see failure reasons inline, survive a page refresh mid-run.

**Step 3.1: Status endpoints (1 day)**

- `GET /jobs` (single aggregate query per the data model note), `GET /jobs/:id`, `GET /jobs/:id/tasks` paginated and filterable by status (failed-tasks view is the primary use).  
- Viewing another member's job: requester and submitter must share a team (membership route, cached 60 s).

**Step 3.2: WebSocket API \+ handlers (1.5 days)**

- API Gateway WebSocket API, `$connect`/`$disconnect`/`subscribe` Lambdas (`workers/ws`), `ws_connections` registry (see Database Design).  RDS Proxy for all handler DB access (ADR-011).

**Step 3.3: Push plumbing \+ catch-up (1.5 days)**

- Delivery worker, after each status write: look up connections subscribed to the job, PostToConnection; `410 Gone` deletes the stale row. Push failures are logged and swallowed, never fail a delivery.  
- Client catch-up: on subscribe ack and every reconnect, refetch `GET /jobs/:id` once, then apply pushes. No server-side replay buffer needed.

**Step 3.4: Frontend chunk 3, dashboard \+ live status (2 days)**

- Job list with aggregate chips, job detail with virtualized 2,000-row task table, status filter, failure reasons inline, WS client with reconnect \+ refetch catch-up.  
- Download button UI included here (per-file action on completed tasks); it's too small for its own chunk. It goes live when M4 ships the endpoint.

### M4: Flow 4, Retrieve a file (1 day)

Demo at the end: the download button from chunk 3 returns the file, and returns 404 for anyone outside the submitter's teams.

**Step 4.1: Team-scoped downloads (1 day)**

- `POST /files/:fileId/download-url`: resolve the file's job submitter, requester must share a team with them, 2-minute presigned GET scoped to the single object key (ADR-002/004). 404, not 403, outside scope, to avoid confirming existence. Bytes never touch application servers.

### M5: Hardening \+ end-to-end verification (2 days)

Success-metric tests from the Blueprint, run against the deployed dev environment, gated before prod:

- p99 submission latency on a 100 × 20 job (2,000 tasks) holds \<500 ms.  
- Partial-failure isolation: one revoked-membership destination → 1 failed / 1,999 completed.  
- Kill the platform mid-run → retries drain the backlog on recovery, DLQ stays empty.  
- Never-uploaded file surfaces `FILE_NOT_UPLOADED`.  
- Security pass: unauthenticated 401s at the gateway, cross-team download 404s, expired presigned URLs fail, gitleaks clean.  
- Manual: throttled-connection upload UX, multipart within-session resume, WS reconnect.

**Total: 32 days** (copy to the Blueprint Release 1 section once ratified).

## Concurrency

| Scenario | Handling |
| :---- | :---- |
| Same task message delivered twice (SQS at-least-once) | Conditional claim UPDATE; terminal statuses ack-and-exit. Platform `source_task_id` unique constraint is the backstop if both run anyway. Net effect: exactly-once outcome from at-least-once delivery. |
| Visibility timeout expires mid-delivery on a slow 1 GB file, second worker picks it up | Both may deliver; platform dedupes on `source_task_id`; both write `completed`. Timeout sized at 6× function timeout to make this rare, not to prevent it. |
| Two tasks finish simultaneously, both try to close the job | `completed_at` set by one conditional UPDATE checking no unresolved tasks exist; row locking serializes it, both outcomes correct. |
| Fan-out crashes mid-batch | Job message redelivered, all task messages resent; duplicates absorbed as above (ADR-008). |
| User submits while uploads still running | Server verifies object existence \+ checksum before delivery regardless of client claims. |
| Two browser tabs, same user | Stateless API, per-file rows, per-tab WS connection rows. Nothing shared to corrupt. |
| Membership revoked between browse and delivery | Time-of-use check at ingest (ADR-013): task fails `NOT_AUTHORIZED_AT_DELIVERY`, correctly. |

## Failure modes

| Dependency down | Effect | Recovery |
| :---- | :---- | :---- |
| Platform service | Deliveries throw, retried via visibility timeout, DLQ after 3; browsing and submission validation return 502 | Backlog drains when platform returns; DLQ consumer marks stragglers failed (visible, not silent) |
| Auth service | Logins fail; workers keep delivering on cached service tokens until expiry, then retry path applies | Smallest, most stable service (ADR-013); ECS restarts unhealthy tasks |
| PostgreSQL | User-facing requests fail; SQS buffers messages (14-day retention) | Messages replay on recovery; idempotency makes replay safe |
| S3 / KMS | Uploads and deliveries fail transiently | Standard retry path; nothing acknowledged is lost |
| SQS publish after job commit | Job stuck pending, no message | Sweeper republishes (step 2.5) |
| WebSocket push path | Dashboard stalls | Non-fatal by design; refetch on reconnect; deliveries unaffected |
| One AZ | ECS runs 2 tasks across AZs behind the ALB; Lambda/SQS/S3 are multi-AZ managed | Meets 99.9% without extra machinery |

## Security & access

- **Authentication:** JWT authorizer at the edge rejects unauthenticated traffic before the VPC (ADR-001); services re-validate (defense in depth; the platform must, since workers reach it without the gateway).  
- **Authorization:** platform checks membership on every read; DocBridge checks at submission; platform re-checks `onBehalfOf` at ingest time of use; downloads check requester-shares-team-with-submitter. Four checkpoints, no trust carried forward.  
- **Secrets:** DB credentials, JWT private key, worker client secret in Secrets Manager, injected at runtime. gitleaks in CI. Secrets referenced by ARN, never as Terraform values.  
- **Files:** SSE-KMS at rest (ADR-003), TLS everywhere, presigned URLs single-object \+ short expiry (ADR-004), content-type allow-list enforced in the POST policy and again at ingest, size ceiling in the POST policy.  
- **Sensitive data inventory:** password hashes (auth DB), clinical documents (staging \+ platform store), everything else metadata. No PHI in logs: task IDs and reasons only; file names appear only in failure reasons shown to authorized users.

## Testing strategy

- **Unit:** token issuance/validation, presigned policy construction, task claim logic, aggregate status derivation, transient-vs-permanent failure classification. Every CI run.  
- **Integration (compose \+ LocalStack):** submit → fan-out → deliver locally; double-delivery of the same task message; checksum-mismatch rejection; DLQ path marks tasks failed. Nightly and on worker changes.  
- **E2E (deployed dev):** the M5 list, gated before any prod apply.

## Launch plan

- **Migration:** none, greenfield. Each service's migrations run from its own folder as a deploy step (forward-only for v1).  
- **Rollout:** M0 → M5 as sequenced; each milestone verified in dev, then prod via the approval gate. Gateway routes added only after services verify internally (ADR-001).  
- **Feature flags:** none for v1, no existing users to protect. The approval gate is the control point.  
- **Rollback:** previous image SHA (application) or previous plan (infra). Non-backward-compatible migrations ship expand-then-contract across two deploys.  
- **Retention:** requirement says 30 days *after job completion*; the S3 lifecycle rule keys off object age, so the implemented guarantee is "≥30 days after completion" (uploads precede completion). Exact-to-completion deletion needs a per-object sweep off `jobs.completed_at`; deferred to v2 unless the compliance framing requires exactness. Worth confirming with the "client".  
- **Monitoring (v1 \= default CloudWatch per requirements), plus two cheap alarms that back success metrics directly:** DLQ depth \> 0 on either DLQ, and delivery worker error rate. Everything else from default dashboards: gateway p99, ALB 5xx, RDS Proxy connections, age of oldest message.

