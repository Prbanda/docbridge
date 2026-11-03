## **DocBridge TDD: PAS-001**

**Document:** Technical Design Document    
**Status:** Draft    
**Blueprint:** [BluePrint](https://docs.google.com/document/d/1wt1WBIwnD9lB0WXZulXeDkU3UGWnI4JNjL1faXuqgRo/edit?tab=t.eewby3fnqalp)  
**Collaborators:** Pranathi Banda  
**Update:** 25 August 2026

**Purpose**

This Technical Design Document describes the implementation details for the DocBridge system.

The High-Level Design (HLD) defines the overall architecture, while this document expands that architecture into implementation-ready specifications including APIs, data models, service responsibilities, workflows, database schema, security, deployment, and testing.

The intended audience includes software engineers, reviewers, QA engineers, and DevOps engineers.

**Rules for implementation:**

- **IaC:** All resources are created by Terraform. Console is used for reading, not writing.  
- **CI/CD:** Every service gets a GitHub Actions workflow when it's created.  
- **Local first:** Every part runs locally via Docker Compose ([LocalStack](https://www.localstack.cloud/) for S3/SQS/KMS)  
- **Secrets:** Nothing sensitive in code. Secrets Manager \+ env variables.  
- **Decisions:** When a part hits a decision with real competing options, stop and write an ADR using the template before implementing. Decisions with one obvious answer get a one-line note in this doc, not an ADR.

## **Build philosophy**

Milestones follow the Blueprint's ([DocBrdige Blueprint](https://docs.google.com/document/d/1wt1WBIwnD9lB0WXZulXeDkU3UGWnI4JNjL1faXuqgRo/edit?tab=t.eewby3fnqalp)) 4 user flows. Each of M1–M6 ends with something a user can test in the browser.

Within a milestone: when a flow touches a service, build that service close to its full project scope, not the thin slice the flow strictly needs. Reopening a service later for one endpoint costs more than building it now. 

## **Estimate compression logic (AI-assisted implementation)**

**Estimates assume AI-assisted implementation** (Claude Code, Cursor, Codex, etc): AI writes most of the boilerplate, Terraform, and tests. Lead engineer will review and verify everything.

Compression applied per work type:

- **Boilerplate** (CRUD endpoints, Terraform modules, compose files, CI YAML, UI scaffolding): compressed 50–60%  
- **Concurrency-sensitive logic** (submission transaction, worker claim/retry, idempotency): compressed 30–40% only. The bottleneck is reasoning about interleavings and testing them, not code volume.  
- **Infra wiring and first-time cloud debugging** (VPC endpoints, IAM policies, ALB health checks, API Gateway \+ VPC Link): compressed minimally. Tied to AWS console debugging, not typing speed.

## **Milestones**

| Milestone | Flow | Estimate | Demonstrable at the end |
| :---- | :---- | :---- | :---- |
| M0 | Foundations  | 4 d | Infra live, pipelines green, local env runs |
| M1 | Flow 1: User Authentication | 4d | Register the user, login  |
| M2 | Flow 2: Browse destinations | 5 d  | Browse the team/binder/folder tree in the browser |
| M3 | Flow 3: Distribute a batch (Upload) | 5 d | Select files, watch uploads, map destinations, submit, get confirmation;  |
| M4 | Flow 4: Background Job/Task processing | 5d | Add job/tasks to the queues, documents land in the platform |
| M5 | Flow 5: Track a job | 6 d | Watch a job's tasks flip live on the dashboard |
| M6 | Flow 6: Retrieve a file | 1 d   | Download button returns the file |
| M7 | Hardening \+ E2E verification | 2 d    | Success metrics proven on the deployed system |
| **Total** |  | 32 d  |  |

## Repository and environment structure

| docbridge/   DocBridge.slnx              \# one solution: services, workers, tools, tests   Directory.Build.props       \# shared MSBuild settings (target framework, nullable, analyzers)   Directory.Packages.props    \# central NuGet versions   global.json                 \# pinned .NET SDK   docker-compose.yml          \# full local stack    .env.example   services/                        auth/                     \# login, token issuance, JWKS, service clients     platform/                      docbridge-api/            \# files, jobs, destinations, staging, job enqueue   workers/                    \# Lambda functions      Lambda.props              \# shared Lambda packaging settings     common/                        fanout/                        delivery/                 \# task queue → platform ingest,failure classification     ws/                       \# WebSocket \$connect / \$disconnect / subscribe     maintenance/              \# EventBridge sweeper, DLQ handling     local-runner/                packages/     shared/                   \# DocBridge.Shared: JWT validation \+ JWKS cache   web/                        \# React \+ Vite SPA     src/       pages/                  \# login       browse/                 \# destination tree        batch/  upload/         \# batch building, hashing worker, presigned upload       audit/                  \# job page and audit log    tools/     db-bootstrap/             \# Lambda: creates per-service databases and roles     seed/                     \# seed data (teams, nested folders, users)   tests/     DocBridge.UnitTests/          \# xUnit v3: tokens, claim logic     DocBridge.IntegrationTests/   \# per-milestone flow tests (M1–M5)    infra/     modules/                  \# reusable Terraform building blocks:                                      app/                    \# composes the building blocks \-full application tier     envs/       global/                 \# account-wide, once per region: ECR repos        dev/                    \# thin config: network \+ rds \+ s3 \+ app dev variables       prod/                   \# same calls, prod variables   bootstrap/                  \# one-time Terraform state backend creation    docker/                     \# local-only images and init: edge (nginx)   scripts/                    \# build-lambdas, deploy, aws-up/down   docs/     design/                   \# requirements, Blueprint , ADRs, TDD     testing/                  \# one testing doc per milestone \+ requirements trace     images/   .github/workflows/          \# one workflow per deployable(\_service.yml reusable) |
| :---- |

2 explicit rules:

1. `infra/envs/*` never duplicates resource blocks, only calls `infra/modules/*` with different variables.   
2. Each service owns its own migrations folder inside its directory, consistent with each service owning its logical database (ADR-005). No central migrations folder.

### 

### **DataBase Design**

**Auth DB**

| CREATE TABLE users (     id              UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     email           VARCHAR(255) UNIQUE NOT NULL,     password\_hash   VARCHAR(255) NOT NULL,     full\_name       VARCHAR(255),     created\_at      TIMESTAMPTZ NOT NULL DEFAULT now() );  |
| :---- |

**“users”** table contains ‘id’, ‘email’, ‘password\_hash’, ‘full\_name’ and ‘created\_at’ columns. ‘Id’ is the primary key, ‘email’ is unique, ‘password\_hash’ is encrypted and stored. Password is hashed using SHA256 hash function as it is commonly used for integrity checks, and is one-way. 

**Platform DB**

| CREATE TABLE teams (     id           UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     name         VARCHAR(255) NOT NULL,     created\_at   TIMESTAMPTZ NOT NULL DEFAULT now(),     updated\_at   TIMESTAMPTZ NOT NULL DEFAULT now(),     created\_by   VARCHAR(255) NOT NULL,     updated\_by   VARCHAR(255) NOT NULL ); CREATE TABLE team\_memberships (     id          UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     user\_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,     team\_id     UUID NOT NULL,     is\_active   BOOLEAN DEFAULT TRUE NOT NULL            created\_at  TIMESTAMPTZ NOT NULL DEFAULT now(),     UNIQUE (user\_id, team\_id) ); CREATE TABLE binders (     id           UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     team\_id      UUID NOT NULL REFERENCES teams(id) ON DELETE RESTRICT,     name         VARCHAR(255) NOT NULL,     created\_at   TIMESTAMPTZ NOT NULL DEFAULT now() ); CREATE INDEX idx\_binders\_team\_id ON binders(team\_id); CREATE TABLE folders (     id             UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     binder\_id      UUID NOT NULL REFERENCES binders(id) ON DELETE RESTRICT,     parent\_folder\_id   UUID REFERENCES folders(id) ON DELETE RESTRICT,     name           VARCHAR(255) NOT NULL,     created\_at     TIMESTAMPTZ NOT NULL DEFAULT now() ); CREATE INDEX idx\_folders\_binder\_id ON folders(binder\_id); CREATE TABLE files (     id             UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     folder\_id      UUID NOT NULL REFERENCES folders(id) ON DELETE RESTRICT,     name           VARCHAR(255) NOT NULL,     s3\_key         VARCHAR(1024) NOT NULL UNIQUE,     size\_bytes     BIGINT NOT NULL CHECK (size\_bytes \>= 0),     checksum       VARCHAR(128) NOT NULL,     content\_type   VARCHAR(255),     uploaded\_by    UUID NOT NULL,              created\_at     TIMESTAMPTZ NOT NULL DEFAULT now()     UNIQUE (folder\_id, name)     ); CREATE INDEX idx\_documents\_folder\_id ON documents(folder\_id); |
| :---- |

**“teams”** table contains details of the teams in the hierarchy. It has ‘id’, ‘name’ and ‘created\_at’ columns. ‘id’ is the primary key for the table. The limitation is we don't have access to create or delete teams from the docBridge UI.

**“team\_memberships”** table contains details of all users with their respective access level to the teams. This table has ‘id’, ‘user\_id’, ‘team\_id’, ‘is\_active’, ‘created\_at’ columns. ‘user\_id’ and ‘team\_id’ make a  unique key. Once a user has given permission to a team(s), a new record for each team is inserted in the table(‘user\_id’ and ‘team\_id’ combination is unique). ‘is\_active’ column is true by default and it indicates the ‘user\_id’ has access to the ‘team\_id’. When the permission for the user is removed, we can make the ‘is\_active’ value false instead of deleting the record.

**“binders”** table has ‘id’, ‘team\_id’, ‘name’ and ‘created\_at’ columns. ‘id’ is the primary key, ‘team\_id’ is the foreign key to the 'teams' table.

**“folders”** table has ‘id’, ‘binder\_id’, ‘parent\_folder\_id’, ‘name’ and ‘created\_at’ columns. ‘id’ is the primary key, ‘binder\_id’ is the foreign key from the binders table, ‘parent\_folder\_id’ can be null and references to the parent folder if it is nested.

**“files”** table has details of all the documents that are uploaded to DocBridge. it contains ‘id’, ‘folder\_id’, ‘name’, ‘s3\_key’, ‘size\_bytes’, ‘checksum’, ‘content\_type’, ‘uploaded\_by’, ‘created\_at’ columns. ‘id’ is the primary key of the table. ‘folder\_id’ is foreign key referring to the folders table. ‘s3\_key’ is the unique identifier for a file stored on S3 bucket. ‘size\_bytes’, ‘checksum’, ‘content\_type’ are the details of the document. Here, ‘folder\_id’ \+ ‘name’ is Unique to avoid storing two files with the same name.

**DocBridge DB**

| CREATE TABLE jobs (     id          UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     folder\_id   UUID NOT NULL,     –-opaque ref \-\> platform.folder\_id,     user\_id     UUID NOT NULL,     \-- opaque ref \-\> auth\_db.users.id     status      VARCHAR(30) NOT NULL DEFAULT 'pending'                   CHECK (status IN ('pending', 'Processing', 'Completed',                 'Completed\_with\_errors', 'failed')),     created\_at         TIMESTAMPTZ NOT NULL DEFAULT now(),     started\_at         TIMESTAMPTZ NOT NULL,     completed\_at       TIMESTAMPTZ NOT NULL ); CREATE INDEX idx\_jobs\_team\_id ON jobs(folder\_id); CREATE INDEX idx\_jobs\_user\_id ON jobs(user\_id); CREATE TABLE tasks (     id                UUID PRIMARY KEY DEFAULT gen\_random\_uuid(),     job\_id            UUID NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,     file\_id           UUID NOT NULL REFERENCES files(id),     team\_id           UUID NOT NULL,     binder\_id         UUID NOT NULL,     folder\_id         UUID NOT NULL, \-- opaque ref-\> platform\_db.folders.id     status            VARCHAR(30) NOT NULL DEFAULT 'pending'                         CHECK (status IN ('pending', 'Processing',                        'Succeeded', 'failed')),     checksum          VARCHAR(128),     retry\_count       INT NOT NULL DEFAULT 0 CHECK (retry\_count \>= 0),     failure\_reason    TEXT,     created\_at        TIMESTAMPTZ NOT NULL DEFAULT now(),     updated\_at        TIMESTAMPTZ NOT NULL DEFAULT now() ); CREATE INDEX idx\_tasks\_job\_id ON tasks(job\_id); CREATE INDEX idx\_tasks\_status ON tasks(status);  |
| :---- |

### **API Design & contracts**

Users register to the DocBridge application by sending “email”, “password” and “full name”.   
Login using “email”, “password”.

**Auth API**

| POST /api/v1/auth/register Request: { “Email”:”user@domain.com”, “Password”: “\*\*\*\*\*\*\*\*”, “name”: “full name” } Response: 201 Created { “Id”:”uuid”, “Name”:”full name”, “Email”: “user@domain.com” } Errors: 409 EMAIL\_ALREADY\_EXISTS POST /api/v1/auth/login Request:   {  "email": "user@domain.com",  "password": "\*\*\*\*\*\*\*\*"  } Response: 200 OK {  "accessToken": "...",  "expiresIn": 3600  } Errors: 401 INVALID\_CREDENTIALS |
| :---- |

**Platform API**

| GET /api/v1/platform/teams Response: 200 OK   {  "teams": \[ { "team\_id": "...", "name": "..." } \]  } Errors: 403 INSUFFICIENT\_PERMISSIONS (caller isn't a team admin) GET /api/v1/platform/teams/{teamId}/binders Response: 200 OK   {  "binders": \[ { "binderId": "...", "name": "..." } \]  } GET /api/v1/platform/binders/{binderid}/folders Response: 200 OK {  "folders": \[ { "folder\_id": "...", "name": "..." } \]  } GET /api/v1/platform/folders/{folderId}/contents Response: 200 OK { Documents: \[{"documentId": "...", "name": "...", ...}\] } //Get the pre-signed URL and make request to retrieve from S3 GET /api/v1/platform/folders/{folderId}/files/{fileId} Response: 200 OK {      {"documentId": "...", "name": "...", ...}  } POST /api/v1/platform/files/{fileId}/download-url Response:  {     “Url”: “...”,     “expiresIn”: 120 }  |
| :---- |

**DocBridge API \- Upload**

| POST /api/v1/docbridge/jobs Request: {   Job:    \[{"teamId": "uuid",   "binderId": "uuid",   "folderId": "uuid",   "files": \[     { "fileName": "document.pdf",        "sizeBytes": 204800,        "checksum": "sha256:..."      }\] }, ...\] } Response: 202 Accepted {   "jobId": "uuid",   "taskCount": 1,   "statusUrl": "/api/v1/docbridge/jobs/uuid",   "uploadTargets": \[     { "taskId": "uuid",        "fileName": "invoice.pdf",         "presignedUploadUrl": "https\://s3..."      },     { "taskId": "uuid",        "fileName": "invoice.pdf",        "presignedUploadUrl": "https\://s3..."      }    \] } Errors: 403 FORBIDDEN (no write access to folder),  400 VALIDATION\_ERROR (empty files array, oversized file)  |
| :---- |

**Job Status(Upload and Download)**

| GET /api/v1/docbridge/jobs/{jobId} Response: 200 OK {   "jobId": "uuid",   "type": "download",   "status": "processing",   "totalTasks": 2,   "succeeded": 1,   "failed": 0,   "tasks": \[     {       "taskId": "uuid",       "fileName": "xxx.pdf",       "status": "succeeded",       "downloadUrl": "https\://s3-presigned-url..."   \-- present only for succeeded download tasks     },     { "taskId": "uuid",        "fileName": "xxx.pdf",        "status": "processing"      },   \] } Errors: 404 JOB\_NOT\_FOUND, 403 FORBIDDEN (job belongs to a different user/team)  |
| :---- |

## **Milestones**

### M0: Foundations (4 days)

**Step 0.1: Monorepo, local environment, shared package (1 day)**

- Repo layout as specified above, built as one .NET solution for all backend projects plus npm for `` `web/ ``\` (ADR-015). `` `Directory.Build.props` `` holds shared build settings and analyzers; `` `Directory.Packages.props` `` pins package versions.  
- `` `DocBridge.Shared` `` class library, kept thin: JWT bearer validation setup against the auth JWKS (one implementation, every service references it), the error envelope `` `{ error: { code, message } }` ``, and options binding. No domain models or data access (ADR-015).  
- Linting: .NET analyzers and `` `dotnet format` `` for the backend; ESLint and the TypeScript config for the SPA.  
- `` `docker-compose.yml` ``: 3 ASP.NET Core service containers, Postgres with an init script creating 3 logical databases \+ 3 roles, LocalStack (S3, SQS). Workers run locally as .NET processes against LocalStack. Meets the "runnable locally" requirement verbatim.

**Step 0.2: Terraform foundation (2 days)**

- `bootstrap/` script creates the state backend (S3 \+ DynamoDB lock), the single manual step (ADR-012).  
- \`infra/modules/\`:  
  \- network: VPC, 2 AZs, private subnets for everything, NAT  
   	 \- rds: single Postgres instance, RDS Proxy, Secrets Manager secrets per logical DB role  
    \- ecr  
  \- s3: staging bucket with SSE-KMS \`aws/s3\` \+ Bucket Keys (ADR-003), Block Public Access, TLS-only policy, 30-day lifecycle expiry, 7-day multipart-abort cleanup; plus SPA hosting bucket \+ CloudFront  
- \`infra/envs/dev\` and \`infra/envs/prod\` call the modules with per-environment variables. No duplicated resource blocks.  
- Bootstrap and migrate run as one-off ECS tasks from the pipeline, not from \`terraform apply\`. First boot: apply (count 0\) → bootstrap → migrate → apply (count 2, authorizer, steady state) → seed.  
- Logical databases and per-service roles are created by the bootstrap job, not by hand (ADR-009). Each service applies its own EF Core migrations from its own \`Migrations/\` folder (ADR-014).

**Step 0.3: CI/CD skeleton, GitHub Actions (1 day)**

- \- Per-service workflow with path filters (\`services/auth/\*\*\` only triggers auth): \`dotnet build\` → \`dotnet test\` → docker build → push to ECR tagged with git SHA → deploy (task definition update).  
- \- Worker workflow: \`dotnet publish\` → zip → update Lambda function code (ADR-015).  
- \- SPA workflow: \`npm ci\` → lint → test → \`vite build\` → sync to the hosting bucket → CloudFront invalidation.  
- \- Infra workflow: \`terraform plan\` as a PR comment, apply on merge, manual approval environment for prod.  
- \- Rollback: \`aws ecs update-service \--task-definition \<previous-arn\>\` followed by a stability wait.

### M1: Flow 1, User Authentication (4 days)

**Step 1.1: Auth service, complete, deployed (2.5 days)**

\- ASP.NET Core service, full scope in one pass per the build philosophy:  
\- registration (bcrypt cost 12, generic duplicate-email error)  
\- login (constant-time compare)  
\- RS256 signing with the keypair in Secrets Manager; JWKS endpoint with \`kid\`  
 \- service token endpoint (client credentials, scope validation, \`token\_use: 'service'\`, ADR-013), even though nothing calls it until the delivery worker in M4  
\- Seed the \`delivery-worker\` client (scope \`documents:ingest\`) via migration; secret in Secrets Manager.  
\- Deploy: ECS Fargate (2 tasks across AZs), internal ALB, health check, verified from inside the VPC (ADR-001 rollout note).

### M2: Flow 2, Browse destinations (5 days)

Demo at the end: a member registers, logs in, and browses the team → binder → folder tree in the browser, filtered to their memberships.

**Step 2.1: Platform hierarchy, membership route, deployed (1 day)**

\- Schema (EF Core, ADR-014) and the three user-token read endpoints, with membership enforced in the query.  
\- \`GET /teams/:teamId/members/:userId\` for service callers (scope \`memberships:read\`): 200 if member, 404 if not.  
\- Demo seed data (5 teams across regions, nested folders) for the ≤50-teams, under-1-second latency test.  
\- Deploy: second ECS service \+ ALB rule.

**Step 2.2: Platform ingest endpoint (1.5 days)**

\- Built now because it completes the platform's project scope. First exercised end to end by the delivery worker in M4; covered by integration tests until then.  
\- \`POST /documents\` (scope \`documents:ingest\`, ADR-013):  
  \- multipart stream, SHA-256 recomputed while streaming to the platform's own store; mismatch returns 422 \`CHECKSUM\_MISMATCH\`  
  \- \`onBehalfOf\` membership checked at time of use; not a member returns 403  
\- \`INSERT … ON CONFLICT (source\_task\_id) DO NOTHING\` via Dapper (ADR-014), returning the existing document with 200 on conflict  
\- content-type allow-list (no executables)

**Step 2.3: Public edge (1.5 days)**

\- API Gateway HTTP API, native JWT authorizer against the auth JWKS, VPC Link to the internal ALB (ADR-001). CORS for the SPA origin, default throttling.  
\- The gateway exposes auth's public routes, \`/.well-known/\*\` and the authorized \`/destinations/\*\` routes. The platform stays internal-only, reached by the DocBridge API and the delivery worker over the internal ALB (ADR-006).  
\- Routes are enumerated one by one, so each later milestone adds its own: \`POST /files\` and \`POST /jobs\` (M3), the \`/jobs\` reads (M5), \`POST /files/:fileId/download-url\` (M6).  
\- Verify: an unauthenticated request returns 401 at the gateway and never reaches the ALB.  
\- Minimal compression on this step: bounded by AWS propagation and debugging, not code.

**Step 2.4: DocBridge API skeleton \+ browsing proxy, deployed (1 day)**

- \- ASP.NET Core service, \`docbridge\` logical DB, EF Core migrations.  
- \- \`/destinations/\*\` proxy the platform with the user's JWT forwarded through a typed \`HttpClient\`. Membership enforcement lives in the platform; DocBridge adds the docbridge-enabled filter (ADR-006).  
- \- 5 s timeout; platform errors mapped to 502\.  
- 

**Step 2.5: Frontend chunk 1, browse destinations UI (1.5 days)**

- \- React \+ Vite SPA on the S3 \+ CloudFront hosting from M0.  
- \- Register and login screens. The token is held in memory; on its 15-minute expiry the SPA sends the user back to login (refresh tokens deferred to v2).  
- \- Lazy destination tree: teams → binders → one folder level per expand. This matches the platform's one-level API, so the under-1-second target applies per level. Multi-select deferred to chunk 2, where mapping needs it.  
- 

### M3,M4: Flow 3,4, Distribute a batch(Upload) (10 days)

Demo at the end: a member selects up to 100 files, watches parallel uploads with progress, maps files to destinations, submits, and gets a confirmation with the task count. Deliveries land in the platform (verified via the platform API), including through kill-and-recover of the platform.

M3 covers Steps 3.1, 3.2 and 3.6 (upload and submit). M4 covers Steps 3.3 to 3.5 (the asynchronous pipeline). The async pipeline steps carry the lightest compression in the plan, deliberately.

**Step 3.1: Presigned uploads, \`POST /files\` (1.5 days)**

- \- Insert \`files\` rows and return a presigned POST per file (ADR-002) with these conditions:  
-   \- \`content-length-range\` capped at the declared size (hard ceiling 1 GB)  
-   \- exact \`Content-Type\` from the allow-list (pdf, docx, xlsx, png, jpg)  
-   \- \`x-amz-checksum-sha256\`, so S3 verifies on write  
- \- URLs expire in 15 minutes (ADR-004). Files over 100 MB get a multipart plan with individually signed part URLs. The bucket already exists from M0.  
- \- Client contract: the browser computes SHA-256 before requesting URLs.  
- 

**\*\*Step 3.2: Job submission transaction, \`POST /jobs\` (1.5 days)\*\***

\- Validate that every \`fileId\` belongs to the caller and is unclaimed.  
\- Validate destinations via \`GET /teams/:teamId/members/:userId\`: one call per distinct team, cached per request.  
\- Expand mappings to task rows, then insert the job \+ up to 2,000 tasks in one transaction (multi-row insert via Dapper, ADR-014).  
\- Publish **\*\*one\*\*** job message and return 201 \`{jobId, taskCount}\`. Constant work per request is how the 500 ms p99 holds (ADR-008).  
\- If the SQS publish fails after commit, still return 201; the sweeper (Step 3.5) republishes. Never fail a submission the database accepted, never lose it silently.

**\*\*Step 3.3: Queues \+ fan-out worker (1.5 days)\*\***

\- Terraform: job queue \+ DLQ, task queue \+ DLQ, \`maxReceiveCount\` 3 (ADR-005). Visibility timeouts: job queue 2 minutes, task queue 6× the delivery timeout.  
\- Fan-out .NET Lambda: load the job's task IDs, \`SendMessageBatch\` in groups of 10 straight to the task queue (ADR-016). Body \`{taskId}\` only, no tokens in queues.  
\- A partial batch failure throws, so SQS redelivers the job message. Redelivery is harmless because per-task idempotency absorbs the duplicates (ADR-008).

**\*\*Step 3.4: Delivery worker (2.5 days)\*\***

\- Built together with fan-out; they are one pipeline, not two features.  
\- Claim (Dapper, ADR-014): \`UPDATE tasks SET status='in\_progress', attempt\_count=attempt\_count+1 WHERE id=\$1 AND status IN ('pending','in\_progress') RETURNING \*\`. A terminal status means ack and exit.  
\- HEAD the S3 object first; missing returns permanent \`FILE\_NOT\_UPLOADED\` (Blueprint risk 3).  
\- Stream S3 GET → incremental SHA-256 → platform \`POST /documents\` in a single pass, never buffering 1 GB in memory (ADR-011).  
\- Service JWT via client credentials, cached in a static field across warm invocations, refreshed on expiry (ADR-013).  
\- Outcomes:

\| Platform response \| Task result \| Queue action \|  
\| \--- \| \--- \| \--- \|  
\| 201 / 200 \| \`completed\` \+ document id \| Ack \|  
\| 403 \| Failed \`NOT\_AUTHORIZED\_AT\_DELIVERY\` (membership revoked after submit) \| Ack, no retry \|  
\| 422 \| Failed \`CHECKSUM\_MISMATCH\` \| Ack, no retry \|  
\| 5xx / timeout \| Unchanged \| Throw; SQS retries via visibility timeout \|

\- \`failure\_reason\` carries the file name, destination and cause.  
\- On any terminal transition, set \`jobs.completed\_at\` if no unresolved tasks remain (conditional \`UPDATE\` with a \`NOT EXISTS\` subquery, race-safe).  
\- All database access goes through RDS Proxy, with a small Npgsql pool per instance (ADR-011).  
\- Reserved concurrency: **\*\*50 is the prod target\*\***; without a cap, a 2,000-task burst can exhaust the RDS Proxy pool. Dev ships at \-1 because this account's Lambda concurrency quota is 10, so a reservation of 50 is impossible until that quota is raised. Tunable in Terraform (\`delivery\_reserved\_concurrency\`).

**\*\*Step 3.5: DLQ consumers \+ safety sweeper (1 day)\*\***

\- DLQ consumer for both DLQs: mark the task (or a job's untouched tasks) failed with \`RETRIES\_EXHAUSTED\` plus the last error. This turns "no silent loss" from a queue guarantee into a user-visible status.  
\- EventBridge sweeper every 15 minutes: republish job messages for stale jobs with no task progress. This covers the commit-then-publish-failure window from Step 3.2 and is idempotent like fan-out.

**\*\*Step 3.6: Frontend chunk 2, select, upload, map, submit (2 days)\*\***

\- File picker (100 max, allow-list mirrored client-side), SHA-256 in a Web Worker.  
\- Upload queue with bounded concurrency (4–6) and per-file progress. URLs requested in waves of 20, re-requested on expiry; multipart with within-session resume.  
\- Mapping UI: files × destinations matrix with select-all shortcuts and a live task-count indicator ("100 files × 20 destinations \= 2,000 deliveries").  
\- Submit is blocked until all mapped uploads complete client-side. The server still verifies before delivery (defense in depth).

**\#\# M5: Flow 5, Track a job (6 days)**

Demo at the end: submit a job, watch the dashboard's task counts and rows flip live, see failure reasons inline, survive a page refresh mid-run.

**\*\*Step 5.1: Status endpoints (1 day)\*\***

\- \`GET /jobs\`: the paginated list computes counts for the whole page in one aggregate query (\`count(\*) FILTER (WHERE status \= …)\`), never one query per job.  
\- \`GET /jobs/:id\`: the job plus its counts.  
\- \`GET /jobs/:id/tasks?status=\&page=\&limit=100\`: paginated and filterable by status (the failed-tasks view is the main use), with \`failureReason\`.  
\- Job status is derived from task counts on read, never stored (ADR-010):  
  \- any \`failed\` and nothing pending or in progress → \`completed\_with\_errors\`  
  \- all \`completed\` → \`completed\`  
  \- otherwise → \`in\_progress\` (or \`pending\` if nothing has started)  
\- Viewing another member's job: requester and submitter must share a team (membership route, cached 60 s).

**\*\*Step 5.2: WebSocket API \+ handlers (1.5 days)\*\***

\- API Gateway WebSocket API with \`\$connect\`, \`\$disconnect\` and \`subscribe\` as .NET Lambdas in \`workers/ws\` (ADR-007, ADR-011).  
\- \`\$connect\` validates the JWT (passed as a query parameter) against the JWKS and inserts a \`ws\_connections\` row.  
\- \`subscribe\` applies the same viewing rule as \`GET /jobs/:id\`.  
\- All handler database access goes through RDS Proxy.

**\*\*Step 5.3: Push plumbing \+ catch-up (1.5 days)\*\***

\- After each status write, the delivery worker looks up connections subscribed to the job and calls \`PostToConnection\` with \`{type: 'task\_update', jobId, taskId, status, failureReason?, counts}\`.  
\- \`410 Gone\` deletes the stale row. Push failures are logged and swallowed and never fail a delivery.  
\- Client catch-up: on subscribe ack and every reconnect, refetch \`GET /jobs/:id\` once, then apply pushes. No server-side replay buffer needed.

**\*\*Step 5.4: Frontend chunk 3, dashboard \+ live status (2 days)\*\***

\- Job list with aggregate chips; job detail with a virtualized 2,000-row task table, status filter and failure reasons inline.  
\- WebSocket client with reconnect \+ refetch catch-up.  
\- Download button UI is included here (a per-file action on completed tasks) because it is too small for its own chunk. It goes live when M6 ships the endpoint.

**\#\# M6: Flow 6, Retrieve a file (1 day)**

Demo at the end: the download button from chunk 3 returns the file, and returns 404 for anyone outside the submitter's teams.

**\*\*Step 6.1: Team-scoped downloads (1 day)\*\***

\- \`POST /files/:fileId/download-url\` resolves the file's job submitter; the requester must share a team with them.  
\- Returns a 2-minute presigned GET scoped to the single object key (ADR-002, ADR-004).  
\- Returns 404, not 403, outside scope, to avoid confirming the file exists. Bytes never touch application servers.

**\#\# M7: Hardening \+ end-to-end verification (2 days)**

Success-metric tests from the Blueprint, run against the deployed dev environment, gated before prod:

\- p99 submission latency on a 100 × 20 job (2,000 tasks) holds under 500 ms.  
\- Partial-failure isolation: one revoked-membership destination → 1 failed / 1,999 completed.  
\- Kill the platform mid-run → retries drain the backlog on recovery, DLQ stays empty.  
\- Never-uploaded file surfaces \`FILE\_NOT\_UPLOADED\`.  
\- Lost job message: block the SQS publish after commit and confirm the sweeper republishes within 15 minutes and the job completes.  
\- Security pass: unauthenticated 401s at the gateway, cross-team download 404s, expired presigned URLs fail, gitleaks clean.  
\- Manual: throttled-connection upload UX, multipart within-session resume, WebSocket reconnect.

**\*\*Total: 32 days\*\*** (copy to the Blueprint Release 1 section once ratified).

* **Concurrency:** There are chances that the “job” table is accessed by more than one task worker at the same time to update “succeeded\_count”, “failed\_count” column values. To fix that we use a single atomic statement to update the row. The same should be handled for the “status” column based on “succeeded\_count”, “failed\_count”.

* **Failure modes:(**To be completed**)**

  * Handle timeout exceptions when the file upload take infinite time(eg. \~30mins)

  * Handle if API goes down.

  * Handle DLQ size increase

* **Security & access:** 

  * **Authentication:** JWT authorizer at the edge rejects unauthenticated traffic before the VPC (ADR-003); services re-validate (defense in depth; the platform must, since workers reach it without the gateway).  
  * **Authorization:** platform checks membership on every read; DocBridge checks at submission; platform re-checks `onBehalfOf` at ingest time of use; downloads check requester-shares-team-with-submitter. Four checkpoints, no trust carried forward.  
  * **Secrets:** DB credentials, JWT private key, worker client secret in Secrets Manager, injected at runtime. gitleaks in CI. Secrets referenced by ARN, never as Terraform values.  
  * **Files:** SSE-KMS at rest (ADR-008), TLS everywhere, presigned URLs single-object \+ short expiry (ADR-005), content-type allow-list enforced in the POST policy and again at ingest, size ceiling in the POST policy.  
  * **Sensitive data inventory:** password hashes (auth DB), clinical documents (staging \+ platform store), everything else metadata. No PHI in logs: task IDs and reasons only; file names appear only in failure reasons shown to authorized users.

  * 

* **Testing:** 

  * Integration testing using postman/Bruno to test API endpoints, which includes authentication, database checks, file storage.

  * Include unit tests to test isolated code and small methods like file validation like size and file types, checking permissions.

  * Test UI functionality like file drag-drop, reliability testing

* **Launch:** 

  * **Migration:** none, greenfield. Each service's migrations run from its own folder as a deploy step (forward-only for v1).  
  * **Rollout:** M0 → M5 as sequenced; each milestone verified in dev, then prod via the approval gate. Gateway routes added only after services verify internally.  
  * **Feature flags:** none for v1, no existing users to protect. The approval gate is the control point.  
  * **Rollback:** previous image SHA (application) or previous plan (infra). Non-backward-compatible migrations ship expand-then-contract across two deploys.  
  * **Retention:** requirement says 30 days *after job completion*; the S3 lifecycle rule keys off object age, so the implemented guarantee is "≥30 days after completion" (uploads precede completion). Exact-to-completion deletion needs a per-object sweep off `jobs.completed_at`; deferred to v2 unless the compliance framing requires exactness. Worth confirming with the "client".  
  * **Monitoring (v1 \= default CloudWatch per requirements), plus two cheap alarms that back success metrics directly:** DLQ depth \&gt; 0 on any DLQ, and delivery worker error rate. Everything else from default dashboards: gateway p99, ALB 5xx, RDS Proxy connections, age of oldest message

