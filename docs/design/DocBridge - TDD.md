# DocBridge TDD: PAS-001

**Document:** Technical Design Document  
**Status:** Draft  
**Blueprint:** [BluePrint](https://docs.google.com/document/d/1wt1WBIwnD9lB0WXZulXeDkU3UGWnI4JNjL1faXuqgRo/edit?tab=t.eewby3fnqalp)  
**Collaborators:** Pranathi Banda  
**Updated:** 25 August 2026

## Purpose

This Technical Design Document describes the implementation details for the DocBridge system.

The High-Level Design (HLD) defines the overall architecture, while this document expands that architecture into implementation-ready specifications including APIs, data models, service responsibilities, workflows, database schema, security, deployment, and testing.

The intended audience includes software engineers, reviewers, QA engineers, and DevOps engineers.

### Rules for implementation

- **IaC:** All resources are created by Terraform. Console is used for reading, not writing.
- **CI/CD:** Every service gets a GitHub Actions workflow when it's created.
- **Local first:** Every part runs locally via Docker Compose ([LocalStack](https://www.localstack.cloud/) for S3/SQS/KMS).
- **Secrets:** Nothing sensitive in code. Secrets Manager + env variables.
- **Decisions:** When a part hits a decision with real competing options, stop and write an ADR using the template before implementing. Decisions with one obvious answer get a one-line note in this doc, not an ADR.

## Build philosophy

Milestones follow the [DocBridge Blueprint](https://docs.google.com/document/d/1wt1WBIwnD9lB0WXZulXeDkU3UGWnI4JNjL1faXuqgRo/edit?tab=t.eewby3fnqalp)'s user flows. Each of M1–M6 ends with something a user can test in the browser.

Within a milestone: when a flow touches a service, build that service close to its full project scope, not the thin slice the flow strictly needs. Reopening a service later for one endpoint costs more than building it now.

## Estimate compression logic (AI-assisted implementation)

**Estimates assume AI-assisted implementation** (Claude Code, Cursor, Codex, etc.): AI writes most of the boilerplate, Terraform, and tests. The lead engineer reviews and verifies everything.

Compression applied per work type:

- **Boilerplate** (CRUD endpoints, Terraform modules, compose files, CI YAML, UI scaffolding): compressed 50–60%.
- **Concurrency-sensitive logic** (submission transaction, worker claim/retry, idempotency): compressed 30–40% only. The bottleneck is reasoning about interleavings and testing them, not code volume.
- **Infra wiring and first-time cloud debugging** (VPC endpoints, IAM policies, ALB health checks, API Gateway + VPC Link): compressed minimally. Tied to AWS console debugging, not typing speed.

## Milestones

| Milestone | Flow                                   | Estimate | Demonstrable at the end                                                 |
| :-------- | :------------------------------------- | :------- | :---------------------------------------------------------------------- |
| M0        | Foundations                            | 4 d      | Infra live, pipelines green, local env runs                             |
| M1        | Flow 1: User Authentication            | 4 d      | Register the user, login                                                |
| M2        | Flow 2: Browse destinations            | 5 d      | Browse the team/binder/folder tree in the browser                       |
| M3        | Flow 3: Distribute a batch (Upload)    | 5 d      | Select files, watch uploads, map destinations, submit, get confirmation |
| M4        | Flow 4: Background Job/Task processing | 5 d      | Add job/tasks to the queues, documents land in the platform             |
| M5        | Flow 5: Track a job                    | 6 d      | Watch a job's tasks flip live on the dashboard                          |
| M6        | Flow 6: Retrieve a file                | 1 d      | Download button returns the file                                        |
| M7        | Hardening + E2E verification           | 2 d      | Success metrics proven on the deployed system                           |
| **Total** |                                        | **32 d** |                                                                         |

## Repository and environment structure

```
docbridge/
  DocBridge.slnx              # one solution: services, workers, tools, tests
  Directory.Build.props       # shared MSBuild settings (target framework, nullable, analyzers)
  Directory.Packages.props    # central NuGet versions
  global.json                 # pinned .NET SDK
  docker-compose.yml          # full local stack
  .env.example
  services/
    auth/                     # login, token issuance, JWKS, service clients
    platform/
    docbridge-api/            # files, jobs, destinations, staging, job enqueue
  workers/                    # Lambda functions
    Lambda.props              # shared Lambda packaging settings
    common/
    fanout/
    delivery/                 # task queue → platform ingest, failure classification
    ws/                       # WebSocket $connect / $disconnect / subscribe
    maintenance/              # EventBridge sweeper, DLQ handling
    local-runner/
  packages/
    shared/                   # DocBridge.Shared: JWT validation + JWKS cache
  web/                        # React + Vite SPA
    src/
      pages/                  # login
      browse/                 # destination tree
      batch/  upload/         # batch building, hashing worker, presigned upload
      audit/                  # job page and audit log
  tools/
    db-bootstrap/             # Lambda: creates per-service databases and roles
    seed/                     # seed data (teams, nested folders, users)
  tests/
    DocBridge.UnitTests/          # xUnit v3: tokens, claim logic
    DocBridge.IntegrationTests/   # per-milestone flow tests (M1–M5)
  infra/
    modules/                  # reusable Terraform building blocks
      app/                    # composes the building blocks into the full application tier
    envs/
      global/                 # account-wide, once per region: ECR repos
      dev/                    # thin config: network + rds + s3 + app with dev variables
      prod/                   # same calls, prod variables
  bootstrap/                  # one-time Terraform state backend creation
  docker/                     # local-only images and init: edge (nginx)
  scripts/                    # build-lambdas, deploy, aws-up/down
  docs/
    design/                   # requirements, Blueprint, ADRs, TDD
    testing/                  # one testing doc per milestone + requirements trace
    images/
  .github/workflows/          # one workflow per deployable (_service.yml reusable)
```

Two explicit rules:

1. `infra/envs/*` never duplicates resource blocks, only calls `infra/modules/*` with different variables.
2. Each service owns its own migrations folder inside its directory, consistent with each service owning its logical database (ADR-005). No central migrations folder.

## Database design

### Auth DB

```sql
CREATE TABLE users (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email           VARCHAR(255) UNIQUE NOT NULL,
    password_hash   VARCHAR(255) NOT NULL,
    full_name       VARCHAR(255),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

**`users`** table contains `id`, `email`, `password_hash`, `full_name` and `created_at` columns. `id` is the primary key, `email` is unique, `password_hash` is encrypted and stored. Password is hashed using SHA256 hash function as it is commonly used for integrity checks, and is one-way.

### Platform DB

```sql
CREATE TABLE teams (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name         VARCHAR(255) NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by   VARCHAR(255) NOT NULL,
    updated_by   VARCHAR(255) NOT NULL
);

CREATE TABLE team_memberships (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    team_id     UUID NOT NULL,
    is_active   BOOLEAN DEFAULT TRUE NOT NULL
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (user_id, team_id)
);

CREATE TABLE binders (
    id           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    team_id      UUID NOT NULL REFERENCES teams(id) ON DELETE RESTRICT,
    name         VARCHAR(255) NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_binders_team_id ON binders(team_id);

CREATE TABLE folders (
    id                 UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    binder_id          UUID NOT NULL REFERENCES binders(id) ON DELETE RESTRICT,
    parent_folder_id   UUID REFERENCES folders(id) ON DELETE RESTRICT,
    name               VARCHAR(255) NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_folders_binder_id ON folders(binder_id);

CREATE TABLE files (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    folder_id      UUID NOT NULL REFERENCES folders(id) ON DELETE RESTRICT,
    name           VARCHAR(255) NOT NULL,
    s3_key         VARCHAR(1024) NOT NULL UNIQUE,
    size_bytes     BIGINT NOT NULL CHECK (size_bytes >= 0),
    checksum       VARCHAR(128) NOT NULL,
    content_type   VARCHAR(255),
    uploaded_by    UUID NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
    UNIQUE (folder_id, name)
);

CREATE INDEX idx_documents_folder_id ON documents(folder_id);
```

**`teams`** table contains details of the teams in the hierarchy. It has `id`, `name` and `created_at` columns. `id` is the primary key for the table. The limitation is we don't have access to create or delete teams from the DocBridge UI.

**`team_memberships`** table contains details of all users with their respective access level to the teams. This table has `id`, `user_id`, `team_id`, `is_active`, `created_at` columns. `user_id` and `team_id` make a unique key. Once a user has been given permission to one or more teams, a new record for each team is inserted in the table (the `user_id` and `team_id` combination is unique). The `is_active` column is true by default and indicates the `user_id` has access to the `team_id`. When the permission for the user is removed, we can set `is_active` to false instead of deleting the record.

**`binders`** table has `id`, `team_id`, `name` and `created_at` columns. `id` is the primary key, `team_id` is the foreign key to the `teams` table.

**`folders`** table has `id`, `binder_id`, `parent_folder_id`, `name` and `created_at` columns. `id` is the primary key, `binder_id` is the foreign key to the `binders` table, and `parent_folder_id` can be null and references the parent folder if it is nested.

**`files`** table has details of all the documents that are uploaded to DocBridge. It contains `id`, `folder_id`, `name`, `s3_key`, `size_bytes`, `checksum`, `content_type`, `uploaded_by`, `created_at` columns. `id` is the primary key of the table. `folder_id` is a foreign key referring to the `folders` table. `s3_key` is the unique identifier for a file stored in the S3 bucket. `size_bytes`, `checksum` and `content_type` are the details of the document. Here, `folder_id` + `name` is unique to avoid storing two files with the same name.

### DocBridge DB

```sql
CREATE TABLE jobs (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    folder_id   UUID NOT NULL,     -- opaque ref -> platform.folder_id
    user_id     UUID NOT NULL,     -- opaque ref -> auth_db.users.id
    status      VARCHAR(30) NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'Processing', 'Completed',
                'Completed_with_errors', 'failed')),
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    started_at         TIMESTAMPTZ NOT NULL,
    completed_at       TIMESTAMPTZ NOT NULL
);

CREATE INDEX idx_jobs_team_id ON jobs(folder_id);
CREATE INDEX idx_jobs_user_id ON jobs(user_id);

CREATE TABLE tasks (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id            UUID NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    file_id           UUID NOT NULL REFERENCES files(id),
    team_id           UUID NOT NULL,
    binder_id         UUID NOT NULL,
    folder_id         UUID NOT NULL, -- opaque ref -> platform_db.folders.id
    status            VARCHAR(30) NOT NULL DEFAULT 'pending'
                      CHECK (status IN ('pending', 'Processing',
                      'Succeeded', 'failed')),
    checksum          VARCHAR(128),
    retry_count       INT NOT NULL DEFAULT 0 CHECK (retry_count >= 0),
    failure_reason    TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_tasks_job_id ON tasks(job_id);
CREATE INDEX idx_tasks_status ON tasks(status);
```

## API design & contracts

### Conventions

- **Base URL:** the API Gateway HTTP API endpoint. Paths have no `/api/v1` prefix; the gateway forwards to the internal ALB, which routes by path: `/auth/*` and `/.well-known/*` → auth, `/destinations*`, `/files*`, `/jobs*` → DocBridge API, `/teams*`, `/binders*`, `/search`, `/documents*`, `/users/*`, `/admin/*` → platform.
- **Authentication:** `Authorization: Bearer <accessToken>` on every route except `POST /auth/register`, `POST /auth/login`, `POST /auth/token` and `GET /.well-known/*`. The gateway's JWT authorizer rejects missing or invalid tokens with 401 before the VPC; each service validates again.
- **JSON:** camelCase property names. Absent optional values are returned as `null`.
- **Errors:** one envelope everywhere: `{ "error": { "code": "VALIDATION_ERROR", "message": "..." } }`. Default codes: `400 VALIDATION_ERROR`, `401 UNAUTHORIZED`, `403 FORBIDDEN`, `404 NOT_FOUND`, `409 CONFLICT`, `502 UPSTREAM_UNAVAILABLE`; endpoint-specific codes are listed below.

### Auth API

**`POST /auth/register`**

```json
// Request
{ "email": "user@domain.com", "password": "**********", "name": "Full Name" }

// Response: 201 Created
{ "id": "uuid", "email": "user@domain.com", "name": "Full Name" }
```

- Email is trimmed and lower-cased; password is 10–128 characters; name is 1–200 characters. Stored as a bcrypt hash (cost 12).
- Errors: `400 VALIDATION_ERROR`, `409 REGISTRATION_FAILED` (deliberately generic: it doesn't reveal that the email is taken).

**`POST /auth/login`**

```json
// Request
{ "email": "user@domain.com", "password": "**********" }

// Response: 200 OK
{ "accessToken": "<RS256 JWT>", "expiresIn": 900 }
```

- 15-minute user token, no refresh token in v1. Unknown emails still run one bcrypt verify, so timing doesn't reveal whether an account exists.
- Errors: `401 INVALID_CREDENTIALS`

**`POST /auth/token`** (service clients, ADR-013)

```json
// Request
{ "clientId": "delivery-worker", "clientSecret": "...", "scope": "documents:ingest memberships:read" }

// Response: 200 OK
{ "accessToken": "<RS256 JWT, token_use: service>", "expiresIn": 900 }
```

- `scope` is optional and space-separated; omitted means all of the client's scopes.
- Errors: `401 INVALID_CLIENT`, `400 INVALID_SCOPE`

**`GET /.well-known/jwks.json`** and **`GET /.well-known/openid-configuration`**

Public signing keys (with `kid`) and the discovery document the gateway's JWT authorizer uses to find them.

### DocBridge API: destinations

Browse proxies to the platform. The user's JWT is forwarded and membership is enforced by the platform; DocBridge only adds the docbridge-enabled filter (ADR-006). One level per call, so the under-1-second target applies per level.

**`GET /destinations/teams`**

```json
// Response: 200 OK
[ { "id": "uuid", "name": "Oncology Team", "region": "us" } ]
```

**`GET /destinations/teams/{teamId}/binders`**

```json
// Response: 200 OK
[ { "id": "uuid", "name": "Trial Master File" } ]
```

- Errors: `403 FORBIDDEN` (not a member), `404 NOT_FOUND` (team doesn't exist)

**`GET /destinations/binders/{binderId}/contents?folderId={folderId}`**

Children of the binder root, or of `folderId` when given.

```json
// Response: 200 OK
{
  "folders": [ { "id": "uuid", "name": "Site 01" } ],
  "documents": [
    {
      "id": "uuid", "name": "protocol.pdf", "sizeBytes": 204800, "contentType": "application/pdf",
      "checksumSha256": "<64 hex>", "uploadedByUserId": "uuid", "createdAt": "2026-10-22T10:00:00Z"
    }
  ]
}
```

- Documents are capped at 500 per level, newest first.
- Errors: `403 FORBIDDEN`, `404 NOT_FOUND` (binder, or folder not in this binder)

**`GET /destinations/search?q={term}`**

Name search across the teams, binders and folders the caller can target. `q` is 2–100 characters; results are capped at 10 teams, 20 binders and 30 folders.

```json
// Response: 200 OK
[
  {
    "type": "folder", "id": "uuid", "name": "Site 01",
    "teamId": "uuid", "teamName": "Oncology Team", "region": "us",
    "binderId": "uuid", "binderName": "Trial Master File", "folderId": "uuid",
    "path": ["Oncology Team", "Trial Master File", "Sites", "Site 01"]
  }
]
```

### DocBridge API: upload

Upload is two calls: create the files and upload the bytes straight to S3 (ADR-002), then submit a job that maps those files to destinations.

**`POST /files`**

```json
// Request (1–100 files)
{
  "files": [
    { "name": "protocol.pdf", "sizeBytes": 204800, "contentType": "application/pdf", "sha256": "<64 hex>" }
  ]
}

// Response: 201 Created, one upload plan per file
[
  {
    "fileId": "uuid",
    "upload": {
      "type": "post",
      "url": "https://<staging-bucket>.s3...",
      "fields": { "key": "staging/<userId>/<fileId>", "Content-Type": "application/pdf", "...": "..." },
      "uploadId": null, "partSize": null, "parts": null,
      "expiresAt": "2026-10-22T10:15:00Z"
    }
  },
  {
    "fileId": "uuid",
    "upload": {
      "type": "multipart",
      "url": null, "fields": null,
      "uploadId": "...",
      "partSize": 67108864,
      "parts": [ { "partNumber": 1, "url": "https://..." } ],
      "expiresAt": "2026-10-22T10:15:00Z"
    }
  }
]
```

- Files up to 100 MB get a presigned POST: the policy pins the exact size, content type and `x-amz-checksum-sha256`, so S3 verifies on write. Larger files (up to 1 GB) get a multipart plan with 64 MB parts and one signed URL per part. URLs expire in 15 minutes (ADR-004).
- Allowed types: PDF, DOCX, XLSX, PNG, JPG. The name can't contain `/` or `\`.
- Errors: `400 VALIDATION_ERROR` (count, name, size, `sha256` format), `422 UNSUPPORTED_FILE_TYPE`

**`POST /files/{fileId}/upload-url`**

Fresh URLs for a file that isn't part of a submitted job yet (an expired wave or an interrupted session).

```json
// Request (optional): pass uploadId to resume a multipart upload instead of starting a new one
{ "uploadId": "..." }

// Response: 200 OK
{ "fileId": "uuid", "upload": { /* same shape as POST /files */ } }
```

- Errors: `404 NOT_FOUND` (not the caller's file), `409 FILE_SUBMITTED`

**`POST /files/{fileId}/multipart/complete`**

```json
// Request
{ "uploadId": "...", "parts": [ { "partNumber": 1, "etag": "\"...\"" } ] }

// Response: 200 OK
{ "fileId": "uuid", "status": "uploaded" }
```

- Errors: `400 VALIDATION_ERROR`, `404 NOT_FOUND`, `409 FILE_SUBMITTED`

**`POST /jobs`**

Optional header `Idempotency-Key` (up to 200 characters): a repeat with the same key returns the original job with 200 instead of creating a second one.

```json
// Request
{
  "mappings": [
    {
      "fileId": "uuid",
      "destinations": [
        { "teamId": "uuid", "binderId": "uuid", "folderId": "uuid", "label": "Oncology / TMF / Site 01" },
        { "teamId": "uuid", "binderId": "uuid", "folderId": null }
      ]
    }
  ]
}

// Response: 201 Created (200 OK on an idempotent replay)
{ "jobId": "uuid", "taskCount": 2 }
```

- One task per file × destination. `folderId: null` targets the binder root; `label` is an optional display name. A repeated destination for the same file counts once.
- Limits: 100 files, 20 distinct destinations, 2,000 tasks per job.
- Membership is checked once per distinct team, then the job row, the file claim and every task row are written in one transaction, followed by one job message (ADR-008).
- Errors: `400 VALIDATION_ERROR` (empty mappings, a file in two mappings, missing `teamId`/`binderId`, limits exceeded), `403 NOT_A_MEMBER`, `403 TEAM_NOT_ENABLED`, `409 FILES_UNAVAILABLE` (a file isn't the caller's or is already in another job)

### DocBridge API: track and retrieve

Job status is derived from task counts on every read, never stored (ADR-010):

| Task counts                               | `aggregateStatus`       |
| :---------------------------------------- | :---------------------- |
| nothing pending or in progress, none failed | `completed`           |
| nothing pending or in progress, some failed | `completed_with_errors` |
| every task still pending                  | `pending`               |
| otherwise                                 | `in_progress`           |

Task statuses: `pending`, `in_progress`, `completed`, `failed`.

**`GET /jobs?page=1&limit=20`**

The caller's own jobs, newest first. `limit` is 1–100 (default 20).

```json
// Response: 200 OK
[
  {
    "jobId": "uuid",
    "submittedByUserId": "uuid",
    "createdAt": "2026-10-22T10:00:00Z",
    "completedAt": null,
    "taskCount": 2000,
    "counts": { "pending": 120, "inProgress": 30, "completed": 1849, "failed": 1 },
    "aggregateStatus": "in_progress"
  }
]
```

**`GET /jobs/{jobId}`**

```json
// Response: 200 OK
{
  "job": { /* same shape as one GET /jobs item */ },
  "files": [
    { "fileId": "uuid", "name": "protocol.pdf", "sizeBytes": 204800, "contentType": "application/pdf", "status": "ready" }
  ]
}
```

- File `status` is the staging copy's state, which drives Download availability: `ready`, `not_uploaded`, or `expired` (older than the 30-day retention).
- Visible to the submitter and to anyone sharing a team with them; everyone else gets `404 NOT_FOUND`.

**`GET /jobs/{jobId}/tasks?status=failed&page=1&limit=100`**

Paginated and filterable by status (the failed-tasks view is the main use). `limit` is 1–500 (default 100).

```json
// Response: 200 OK
{
  "total": 1,
  "page": 1,
  "limit": 100,
  "tasks": [
    {
      "taskId": "uuid", "fileId": "uuid", "fileName": "protocol.pdf",
      "teamId": "uuid", "binderId": "uuid", "folderId": "uuid", "destinationLabel": "Oncology / TMF / Site 01",
      "region": "us", "status": "failed", "attemptCount": 1,
      "failureReason": "NOT_AUTHORIZED_AT_DELIVERY: ...", "platformDocumentId": null,
      "updatedAt": "2026-10-22T10:01:00Z"
    }
  ]
}
```

- Errors: `400 VALIDATION_ERROR` (unknown `status`), `404 NOT_FOUND`

**`POST /files/{fileId}/download-url`**

```json
// Response: 200 OK
{ "url": "https://<staging-bucket>.s3...", "expiresIn": 120 }
```

- The requester must share a team with the job's submitter (or own the file, before it is submitted). A 2-minute presigned GET for that one object; bytes go straight from S3 to the browser (ADR-002, ADR-004).
- Errors: `404 NOT_FOUND` outside that scope, never 403, so the file's existence isn't confirmed.

### WebSocket API (ADR-007)

Connect to `wss://<websocket-endpoint>?token=<accessToken>`; browsers can't set headers on the WebSocket handshake, so the JWT goes in the query string (excluded from access logs). `$connect` rejects missing, invalid or service tokens.

```json
// Client → server
{ "action": "subscribe", "jobId": "uuid" }

// Server → client: subscription acknowledged (the client then refetches GET /jobs/{jobId} once to catch up)
{ "type": "subscribed", "jobId": "uuid" }

// Server → client: job not visible to this user
{ "type": "error", "code": "NOT_FOUND", "jobId": "uuid" }

// Server → client: pushed by the delivery worker after every task status change
{
  "type": "task_update",
  "jobId": "uuid",
  "taskId": "uuid",
  "status": "completed",
  "failureReason": null,
  "counts": { "pending": 0, "inProgress": 0, "completed": 2000, "failed": 0 },
  "aggregateStatus": "completed"
}
```

### Platform API (internal)

The platform sits behind the internal ALB. The SPA reaches it only through `/destinations/*`; the DocBridge API and the workers call it directly.

**User-token reads** (membership enforced in the query; `docbridgeEnabled=true` filters to DocBridge-enabled teams):

| Route                                          | Response                                       |
| :--------------------------------------------- | :--------------------------------------------- |
| `GET /teams?docbridgeEnabled=true`             | `[ { id, name, region } ]`                     |
| `GET /teams/{teamId}/binders`                  | `[ { id, name } ]`                             |
| `GET /binders/{binderId}/contents?folderId=`   | `{ folders: [...], documents: [...] }`         |
| `GET /search?q=&docbridgeEnabled=true`         | `[ search hit ]` (shape as `/destinations/search`) |

**Service-token routes:**

| Route                                    | Scope              | Response                                                                 |
| :--------------------------------------- | :----------------- | :----------------------------------------------------------------------- |
| `GET /teams/{teamId}/members/{userId}`   | `memberships:read` | `200 { addedAt }` if a member, `404 NOT_A_MEMBER` if not; the status code is the answer |
| `GET /users/{userId}/teams`              | `memberships:read` | `{ teamIds: [...] }`, used for "requester shares a team with submitter"  |
| `POST /documents`                        | `documents:ingest` | `201 { documentId }`, or `200 { documentId }` when the task was already ingested |

**`POST /documents`** is `multipart/form-data` with a JSON `metadata` part followed by the `file` part:

```json
// metadata part
{
  "taskId": "uuid", "binderId": "uuid", "folderId": "uuid", "name": "protocol.pdf",
  "contentType": "application/pdf", "checksumSha256": "<64 hex>", "onBehalfOf": "<submitting userId>"
}
```

- The file streams to a temp file while SHA-256 is computed, so a 1 GB file is never held in memory and a mismatched file is never stored. Idempotent on `taskId` (`ON CONFLICT (source_task_id) DO NOTHING`).
- Errors: `403 NOT_A_MEMBER` (`onBehalfOf` lost membership after submission), `404 DESTINATION_NOT_FOUND`, `422 CHECKSUM_MISMATCH`

**Admin routes** (scope `platform:admin`, used by seeding and setup):

| Route                                           | Request                              | Response                    |
| :---------------------------------------------- | :----------------------------------- | :-------------------------- |
| `POST /admin/teams`                             | `{ name, region, docbridgeEnabled }` | `201 { id, name, region }`  |
| `POST /admin/teams/{teamId}/members`            | `{ userId }`                         | `204`                       |
| `DELETE /admin/teams/{teamId}/members/{userId}` |                                      | `204`                       |
| `POST /admin/teams/{teamId}/binders`            | `{ name }`                           | `201 { id, name }`          |
| `POST /admin/binders/{binderId}/folders`        | `{ name, parentFolderId }`           | `201 { id, name }`          |

## Milestone details

### M0: Foundations (4 days)

**Step 0.1: Monorepo, local environment, shared package (1 day)**

- Repo layout as specified above, built as one .NET solution for all backend projects plus npm for `web/` (ADR-015). `Directory.Build.props` holds shared build settings and analyzers; `Directory.Packages.props` pins package versions.
- `DocBridge.Shared` class library, kept thin: JWT bearer validation setup against the auth JWKS (one implementation, every service references it), the error envelope `{ error: { code, message } }`, and options binding. No domain models or data access (ADR-015).
- Linting: .NET analyzers and `dotnet format` for the backend; ESLint and the TypeScript config for the SPA.
- `docker-compose.yml`: 3 ASP.NET Core service containers, Postgres with an init script creating 3 logical databases + 3 roles, LocalStack (S3, SQS). Workers run locally as .NET processes against LocalStack. Meets the "runnable locally" requirement verbatim.

**Step 0.2: Terraform foundation (2 days)**

- `bootstrap/` script creates the state backend (S3 + DynamoDB lock), the single manual step (ADR-012).
- `infra/modules/`:
  - `network`: VPC, 2 AZs, private subnets for everything, NAT
  - `rds`: single Postgres instance, RDS Proxy, Secrets Manager secrets per logical DB role
  - `ecr`
  - `s3`: staging bucket with SSE-KMS `aws/s3` + Bucket Keys (ADR-003), Block Public Access, TLS-only policy, 30-day lifecycle expiry, 7-day multipart-abort cleanup; plus SPA hosting bucket + CloudFront
- `infra/envs/dev` and `infra/envs/prod` call the modules with per-environment variables. No duplicated resource blocks.
- Bootstrap and migrate run as one-off ECS tasks from the pipeline, not from `terraform apply`. First boot: apply (count 0) → bootstrap → migrate → apply (count 2, authorizer, steady state) → seed.
- Logical databases and per-service roles are created by the bootstrap job, not by hand (ADR-009). Each service applies its own EF Core migrations from its own `Migrations/` folder (ADR-014).

**Step 0.3: CI/CD skeleton, GitHub Actions (1 day)**

- Per-service workflow with path filters (`services/auth/**` only triggers auth): `dotnet build` → `dotnet test` → docker build → push to ECR tagged with git SHA → deploy (task definition update).
- Worker workflow: `dotnet publish` → zip → update Lambda function code (ADR-015).
- SPA workflow: `npm ci` → lint → test → `vite build` → sync to the hosting bucket → CloudFront invalidation.
- Infra workflow: `terraform plan` as a PR comment, apply on merge, manual approval environment for prod.
- Rollback: `aws ecs update-service --task-definition <previous-arn>` followed by a stability wait.

### M1: Flow 1, User Authentication (4 days)

**Step 1.1: Auth service, complete, deployed (2.5 days)**

- ASP.NET Core service, full scope in one pass per the build philosophy:
  - registration (bcrypt cost 12, generic duplicate-email error)
  - login (constant-time compare)
  - RS256 signing with the keypair in Secrets Manager; JWKS endpoint with `kid`
  - service token endpoint (client credentials, scope validation, `token_use: 'service'`, ADR-013), even though nothing calls it until the delivery worker in M4
- Seed the `delivery-worker` client (scope `documents:ingest`) via migration; secret in Secrets Manager.
- Deploy: ECS Fargate (2 tasks across AZs), internal ALB, health check, verified from inside the VPC (ADR-001 rollout note).

### M2: Flow 2, Browse destinations (5 days)

Demo at the end: a member registers, logs in, and browses the team → binder → folder tree in the browser, filtered to their memberships.

**Step 2.1: Platform hierarchy, membership route, deployed (1 day)**

- Schema (EF Core, ADR-014) and the three user-token read endpoints, with membership enforced in the query.
- `GET /teams/:teamId/members/:userId` for service callers (scope `memberships:read`): 200 if member, 404 if not.
- Demo seed data (5 teams across regions, nested folders) for the ≤50-teams, under-1-second latency test.
- Deploy: second ECS service + ALB rule.

**Step 2.2: Platform ingest endpoint (1.5 days)**

- Built now because it completes the platform's project scope. First exercised end to end by the delivery worker in M4; covered by integration tests until then.
- `POST /documents` (scope `documents:ingest`, ADR-013):
  - multipart stream, SHA-256 recomputed while streaming to the platform's own store; mismatch returns 422 `CHECKSUM_MISMATCH`
  - `onBehalfOf` membership checked at time of use; not a member returns 403
  - `INSERT … ON CONFLICT (source_task_id) DO NOTHING` via Dapper (ADR-014), returning the existing document with 200 on conflict
  - content-type allow-list (no executables)

**Step 2.3: Public edge (1.5 days)**

- API Gateway HTTP API, native JWT authorizer against the auth JWKS, VPC Link to the internal ALB (ADR-001). CORS for the SPA origin, default throttling.
- The gateway exposes auth's public routes, `/.well-known/*` and the authorized `/destinations/*` routes. The platform stays internal-only, reached by the DocBridge API and the delivery worker over the internal ALB (ADR-006).
- Routes are enumerated one by one, so each later milestone adds its own: `POST /files` and `POST /jobs` (M3), the `/jobs` reads (M5), `POST /files/:fileId/download-url` (M6).
- Verify: an unauthenticated request returns 401 at the gateway and never reaches the ALB.
- Minimal compression on this step: bounded by AWS propagation and debugging, not code.

**Step 2.4: DocBridge API skeleton + browsing proxy, deployed (1 day)**

- ASP.NET Core service, `docbridge` logical DB, EF Core migrations.
- `/destinations/*` proxy the platform with the user's JWT forwarded through a typed `HttpClient`. Membership enforcement lives in the platform; DocBridge adds the docbridge-enabled filter (ADR-006).
- 5 s timeout; platform errors mapped to 502.

**Step 2.5: Frontend chunk 1, browse destinations UI (1.5 days)**

- React + Vite SPA on the S3 + CloudFront hosting from M0.
- Register and login screens. The token is held in memory; on its 15-minute expiry the SPA sends the user back to login (refresh tokens deferred to v2).
- Lazy destination tree: teams → binders → one folder level per expand. This matches the platform's one-level API, so the under-1-second target applies per level. Multi-select deferred to chunk 2, where mapping needs it.

### M3, M4: Flows 3 and 4, Distribute a batch (Upload) (10 days)

Demo at the end: a member selects up to 100 files, watches parallel uploads with progress, maps files to destinations, submits, and gets a confirmation with the task count. Deliveries land in the platform (verified via the platform API), including through kill-and-recover of the platform.

M3 covers Steps 3.1, 3.2 and 3.6 (upload and submit). M4 covers Steps 3.3 to 3.5 (the asynchronous pipeline). The async pipeline steps carry the lightest compression in the plan, deliberately.

**Step 3.1: Presigned uploads, `POST /files` (1.5 days)**

- Insert `files` rows and return a presigned POST per file (ADR-002) with these conditions:
  - `content-length-range` capped at the declared size (hard ceiling 1 GB)
  - exact `Content-Type` from the allow-list (pdf, docx, xlsx, png, jpg)
  - `x-amz-checksum-sha256`, so S3 verifies on write
- URLs expire in 15 minutes (ADR-004). Files over 100 MB get a multipart plan with individually signed part URLs. The bucket already exists from M0.
- Client contract: the browser computes SHA-256 before requesting URLs.

**Step 3.2: Job submission transaction, `POST /jobs` (1.5 days)**

- Validate that every `fileId` belongs to the caller and is unclaimed.
- Validate destinations via `GET /teams/:teamId/members/:userId`: one call per distinct team, cached per request.
- Expand mappings to task rows, then insert the job + up to 2,000 tasks in one transaction (multi-row insert via Dapper, ADR-014).
- Publish **one** job message and return 201 `{jobId, taskCount}`. Constant work per request is how the 500 ms p99 holds (ADR-008).
- If the SQS publish fails after commit, still return 201; the sweeper (Step 3.5) republishes. Never fail a submission the database accepted, never lose it silently.

**Step 3.3: Queues + fan-out worker (1.5 days)**

- Terraform: job queue + DLQ, task queue + DLQ, `maxReceiveCount` 3 (ADR-005). Visibility timeouts: job queue 2 minutes, task queue 6× the delivery timeout.
- Fan-out .NET Lambda: load the job's task IDs, `SendMessageBatch` in groups of 10 straight to the task queue (ADR-016). Body `{taskId}` only, no tokens in queues.
- A partial batch failure throws, so SQS redelivers the job message. Redelivery is harmless because per-task idempotency absorbs the duplicates (ADR-008).

**Step 3.4: Delivery worker (2.5 days)**

- Built together with fan-out; they are one pipeline, not two features.
- Claim (Dapper, ADR-014): `UPDATE tasks SET status='in_progress', attempt_count=attempt_count+1 WHERE id=$1 AND status IN ('pending','in_progress') RETURNING *`. A terminal status means ack and exit.
- HEAD the S3 object first; missing returns permanent `FILE_NOT_UPLOADED` (Blueprint risk 3).
- Stream S3 GET → incremental SHA-256 → platform `POST /documents` in a single pass, never buffering 1 GB in memory (ADR-011).
- Service JWT via client credentials, cached in a static field across warm invocations, refreshed on expiry (ADR-013).
- Outcomes:

  | Platform response | Task result                                                           | Queue action                              |
  | :---------------- | :-------------------------------------------------------------------- | :---------------------------------------- |
  | 201 / 200         | `completed` + document id                                             | Ack                                       |
  | 403               | Failed `NOT_AUTHORIZED_AT_DELIVERY` (membership revoked after submit) | Ack, no retry                             |
  | 422               | Failed `CHECKSUM_MISMATCH`                                            | Ack, no retry                             |
  | 5xx / timeout     | Unchanged                                                             | Throw; SQS retries via visibility timeout |

- `failure_reason` carries the file name, destination and cause.
- On any terminal transition, set `jobs.completed_at` if no unresolved tasks remain (conditional `UPDATE` with a `NOT EXISTS` subquery, race-safe).
- All database access goes through RDS Proxy, with a small Npgsql pool per instance (ADR-011).
- Reserved concurrency: **50 is the prod target**; without a cap, a 2,000-task burst can exhaust the RDS Proxy pool. Dev ships at -1 because this account's Lambda concurrency quota is 10, so a reservation of 50 is impossible until that quota is raised. Tunable in Terraform (`delivery_reserved_concurrency`).

**Step 3.5: DLQ consumers + safety sweeper (1 day)**

- DLQ consumer for both DLQs: mark the task (or a job's untouched tasks) failed with `RETRIES_EXHAUSTED` plus the last error. This turns "no silent loss" from a queue guarantee into a user-visible status.
- EventBridge sweeper every 15 minutes: republish job messages for stale jobs with no task progress. This covers the commit-then-publish-failure window from Step 3.2 and is idempotent like fan-out.

**Step 3.6: Frontend chunk 2, select, upload, map, submit (2 days)**

- File picker (100 max, allow-list mirrored client-side), SHA-256 in a Web Worker.
- Upload queue with bounded concurrency (4–6) and per-file progress. URLs requested in waves of 20, re-requested on expiry; multipart with within-session resume.
- Mapping UI: files × destinations matrix with select-all shortcuts and a live task-count indicator ("100 files × 20 destinations = 2,000 deliveries").
- Submit is blocked until all mapped uploads complete client-side. The server still verifies before delivery (defense in depth).

### M5: Flow 5, Track a job (6 days)

Demo at the end: submit a job, watch the dashboard's task counts and rows flip live, see failure reasons inline, survive a page refresh mid-run.

**Step 5.1: Status endpoints (1 day)**

- `GET /jobs`: the paginated list computes counts for the whole page in one aggregate query (`count(*) FILTER (WHERE status = …)`), never one query per job.
- `GET /jobs/:id`: the job plus its counts.
- `GET /jobs/:id/tasks?status=&page=&limit=100`: paginated and filterable by status (the failed-tasks view is the main use), with `failureReason`.
- Job status is derived from task counts on read, never stored (ADR-010):
  - any `failed` and nothing pending or in progress → `completed_with_errors`
  - all `completed` → `completed`
  - otherwise → `in_progress` (or `pending` if nothing has started)
- Viewing another member's job: requester and submitter must share a team (membership route, cached 60 s).

**Step 5.2: WebSocket API + handlers (1.5 days)**

- API Gateway WebSocket API with `$connect`, `$disconnect` and `subscribe` as .NET Lambdas in `workers/ws` (ADR-007, ADR-011).
- `$connect` validates the JWT (passed as a query parameter) against the JWKS and inserts a `ws_connections` row.
- `subscribe` applies the same viewing rule as `GET /jobs/:id`.
- All handler database access goes through RDS Proxy.

**Step 5.3: Push plumbing + catch-up (1.5 days)**

- After each status write, the delivery worker looks up connections subscribed to the job and calls `PostToConnection` with `{type: 'task_update', jobId, taskId, status, failureReason?, counts}`.
- `410 Gone` deletes the stale row. Push failures are logged and swallowed and never fail a delivery.
- Client catch-up: on subscribe ack and every reconnect, refetch `GET /jobs/:id` once, then apply pushes. No server-side replay buffer needed.

**Step 5.4: Frontend chunk 3, dashboard + live status (2 days)**

- Job list with aggregate chips; job detail with a virtualized 2,000-row task table, status filter and failure reasons inline.
- WebSocket client with reconnect + refetch catch-up.
- Download button UI is included here (a per-file action on completed tasks) because it is too small for its own chunk. It goes live when M6 ships the endpoint.

### M6: Flow 6, Retrieve a file (1 day)

Demo at the end: the download button from chunk 3 returns the file, and returns 404 for anyone outside the submitter's teams.

**Step 6.1: Team-scoped downloads (1 day)**

- `POST /files/:fileId/download-url` resolves the file's job submitter; the requester must share a team with them.
- Returns a 2-minute presigned GET scoped to the single object key (ADR-002, ADR-004).
- Returns 404, not 403, outside scope, to avoid confirming the file exists. Bytes never touch application servers.

### M7: Hardening + end-to-end verification (2 days)

Success-metric tests from the Blueprint, run against the deployed dev environment, gated before prod:

- p99 submission latency on a 100 × 20 job (2,000 tasks) holds under 500 ms.
- Partial-failure isolation: one revoked-membership destination → 1 failed / 1,999 completed.
- Kill the platform mid-run → retries drain the backlog on recovery, DLQ stays empty.
- Never-uploaded file surfaces `FILE_NOT_UPLOADED`.
- Lost job message: block the SQS publish after commit and confirm the sweeper republishes within 15 minutes and the job completes.
- Security pass: unauthenticated 401s at the gateway, cross-team download 404s, expired presigned URLs fail, gitleaks clean.
- Manual: throttled-connection upload UX, multipart within-session resume, WebSocket reconnect.

**Total: 32 days** (copy to the Blueprint Release 1 section once ratified).

## Concurrency

There are chances that the `jobs` table is accessed by more than one task worker at the same time to update the `succeeded_count` and `failed_count` column values. To fix that we use a single atomic statement to update the row. The same should be handled for the `status` column based on `succeeded_count` and `failed_count`.

## Failure modes

_To be completed._

- Handle timeout exceptions when the file upload takes too long (e.g. ~30 mins).
- Handle the API going down.
- Handle DLQ size increase.

## Security & access

- **Authentication:** JWT authorizer at the edge rejects unauthenticated traffic before the VPC (ADR-003); services re-validate (defense in depth; the platform must, since workers reach it without the gateway).
- **Authorization:** platform checks membership on every read; DocBridge checks at submission; platform re-checks `onBehalfOf` at ingest time of use; downloads check requester-shares-team-with-submitter. Four checkpoints, no trust carried forward.
- **Secrets:** DB credentials, JWT private key, worker client secret in Secrets Manager, injected at runtime. gitleaks in CI. Secrets referenced by ARN, never as Terraform values.
- **Files:** SSE-KMS at rest (ADR-008), TLS everywhere, presigned URLs single-object + short expiry (ADR-005), content-type allow-list enforced in the POST policy and again at ingest, size ceiling in the POST policy.
- **Sensitive data inventory:** password hashes (auth DB), clinical documents (staging + platform store), everything else metadata. No PHI in logs: task IDs and reasons only; file names appear only in failure reasons shown to authorized users.

## Testing

- Integration testing using Postman/Bruno to test API endpoints, which includes authentication, database checks and file storage.
- Unit tests for isolated code and small methods, such as file validation (size and file types) and permission checks.
- UI functionality tests such as file drag-and-drop, and reliability testing.

## Launch

- **Migration:** none, greenfield. Each service's migrations run from its own folder as a deploy step (forward-only for v1).
- **Rollout:** M0 → M5 as sequenced; each milestone verified in dev, then prod via the approval gate. Gateway routes added only after services verify internally.
- **Feature flags:** none for v1, no existing users to protect. The approval gate is the control point.
- **Rollback:** previous image SHA (application) or previous plan (infra). Non-backward-compatible migrations ship expand-then-contract across two deploys.
- **Retention:** requirement says 30 days _after job completion_; the S3 lifecycle rule keys off object age, so the implemented guarantee is "≥30 days after completion" (uploads precede completion). Exact-to-completion deletion needs a per-object sweep off `jobs.completed_at`; deferred to v2 unless the compliance framing requires exactness. Worth confirming with the "client".
- **Monitoring** (v1 = default CloudWatch per requirements), plus two cheap alarms that back success metrics directly: DLQ depth > 0 on any DLQ, and delivery worker error rate. Everything else from default dashboards: gateway p99, ALB 5xx, RDS Proxy connections, age of oldest message.
