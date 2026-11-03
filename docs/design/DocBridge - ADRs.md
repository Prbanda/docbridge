**ADR-001: Edge architecture for public API traffic**

**Context:**

A browser SPA calls three backend services (auth, DocBridge API, document platform), all ASP.NET Core containers on ECS inside a VPC. Every endpoint except login, registration, service-token issue and the JWKS endpoint must reject unauthenticated requests.

**Decision Required:**

How does public API traffic get from the internet to the internal services?

| Decision                                                      | Rationale                                                                                                                                | Trade-offs                                                                                                                                   |
| :------------------------------------------------------------ | :--------------------------------------------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------- |
| Single public ALB, path rules to one target group per service | One routing layer; native ECS fit; cheapest at this traffic                                                                              | Public-facing entry point with no network boundary in front of it JWT validation only inside each service; no throttling without building it |
| **API Gateway HTTP API → VPC Link → internal ALB → services** | Native JWT authorizer rejects bad tokens before traffic enters the VPC Managed throttling; services and ALB live only in private subnets | Two routing layers to configure and debug; extra per-request cost                                                                            |
| API Gateway only, direct integrations to services             | One managed edge with auth and throttling                                                                                                | Still needs a VPC Link plus a load balancer or Cloud Map to reach ECS; loses per-service target groups and health-check routing              |

**Decision:** API Gateway HTTP API → VPC Link → internal ALB → services.

\- HTTP API rather than REST API: it has a native JWT authorizer and VPC Link at about a third of the cost.

\- Public routes (register, login, \`/auth/token\`, \`/.well-known/jwks.json\`) are configured without the authorizer.

\- Services still validate the JWT themselves (ASP.NET Core JWT bearer against the same JWKS). The platform must, because workers reach it without the gateway.

\- Rollout: deploy services and the internal ALB first, verify from inside the VPC, then add the gateway in front.

**ADR-002: File transfer path between browser and staging store**

**Context:**

Users upload up to 100 files per session (average 2 MB, maximum 1 GB), and team members later download staged files. The UI must never freeze during upload, integrity must be verifiable end to end, and submission must be acknowledged in under 500 ms. Staged files live in S3 (encrypted per ADR-003); S3 over database storage is a one-line call, since 1 GB blobs do not belong in PostgreSQL.

**Decision Required:**

Do file bytes move between the browser and S3 directly (presigned URLs), or through our API servers?

| Decision                                                                                 | Rationale                                                                                                                                                                                                                                                                 | Trade-offs                                                                                                                                                             |
| :--------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Presigned transfers both ways: presigned POST for upload, presigned GET for download** | API tier scales with request count, not bandwidth; a 1 GB transfer never holds an API connection POST policies enforce a size ceiling (\`content-length-range\`) and content type at S3 Client SHA-256 is verified by S3 on write and again by the worker before delivery | Upload completion is not observed in-band; the system must verify the object before delivery A presigned URL is a bearer credential while valid (mitigated by ADR-004) |
| Proxy all bytes through the API servers                                                  | API sees every upload succeed or fail; single place to scan files                                                                                                                                                                                                         | API must scale with file bandwidth; every byte moves twice; resumable upload built by hand                                                                             |
| Hybrid: proxy uploads, presign downloads                                                 | In-band observation of uploads                                                                                                                                                                                                                                            | Keeps all of Option 2's upload scaling problems; two mechanisms to secure and test                                                                                     |

**Decision:** Presigned transfers both ways: presigned POST for upload, presigned GET for download

\- Every URL is issued only after an authorization check, is scoped to one object key, and expires per ADR-004.

\- POST, not PUT, because only POST policies can cap object size.

\- Workers HEAD the object and verify the checksum before delivery; a missing object fails the task with \`FILE_NOT_UPLOADED\`.

\- Files over about 100 MB use S3 multipart with individually signed parts, giving resume-from-part within a session.

**ADR-003: Encryption at rest for staged files**

**Context:**

Staged files are clinical trial documents and must be encrypted at rest by a dedicated cloud key management service; implementing our own cryptography is forbidden. Files reach S3 directly from the browser (ADR-002), so no key material can be needed on the client.

**Decision Required:**

Which S3 server-side encryption mode does the staging bucket use?

| Decision                                        | Rationale                                                                                                                                                     | Trade-offs                                                                                          |
| :---------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------ | :-------------------------------------------------------------------------------------------------- |
| SSE-S3 (S3-managed keys)                        | Zero configuration, no KMS cost                                                                                                                               | No key policy, revocation or key-usage audit trail; weakest for a compliance domain                 |
| **SSE-KMS with the AWS-managed \`aws/s3\` key** | One bucket setting; presigned transfers work unchanged; key usage logged in CloudTrail Meets the requirement as written: KMS-managed, no crypto written by us | Key policy is fixed; the key cannot be disabled, so it is not an access boundary or kill switch     |
| SSE-KMS with a customer-managed key (CMK)       | We own policy, rotation and revocation; decryption becomes an extra authorization check                                                                       | Key resource and policy to manage; per-request KMS charges                                          |
| SSE-C (customer-provided keys)                  |                                                                                                                                                               | Raw key bytes on every request would have to reach the browser through presigned URLs; unacceptable |

**Decision:** Option 2, with S3 Bucket Keys enabled to cut KMS request cost.

\- Access control stays where it already lives: IAM, the bucket policy, and authorized URL issuance.

\- Upgrade path: switch to a CMK (Option 3\) if a client or auditor requires customer control of the key. It is a bucket setting plus one key resource, invisible to application code, and the 30-day lifecycle re-keys the whole bucket within a month.

**ADR-004: Presigned URL expiry policy**

**Context:**

A presigned URL is a bearer credential until it expires, so expiry is the main control on exposure. Upload URLs are queued by the browser for batches of up to 100 files and must tolerate 1 GB files on slow links. Download URLs are clicked immediately. S3 validates the signature when a request starts, so a transfer begun before expiry finishes even if expiry passes mid-transfer.

**Decision Required:**

How long do presigned upload (POST) and download (GET) URLs stay valid?

| Decision                                                        | Rationale                                                                                                                 | Trade-offs                                                                            |
| :-------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------ | :------------------------------------------------------------------------------------ |
| Long expiry for both (about 12 hours)                           | Users can walk away and resume; fewest URL requests                                                                       | A leaked URL exposes clinical documents for hours, a standing bypass of authorization |
| **Short and asymmetric: 15 minutes upload, 2 minutes download** | Exposure window of minutes; downloads are effectively single-use 15 minutes covers time-to-start for queued batch uploads | An interrupted session needs fresh URLs; the client requests upload URLs in waves     |

**Decision:** Short and asymmetric: 15 minutes upload, 2 minutes download

\- Downloads get the tightest practical window because they are used at once; uploads get slack for queuing.

\- The client requests upload URLs in waves of at most 20 and re-requests on expiry.

\- Both values are configuration, adjustable from evidence without redesign. Expiry is the second layer: every URL is already scoped to one object key and one operation.

**ADR-005: Task queue technology for asynchronous delivery**

**Context:**

One submission fans out into up to 2,000 independent tasks; the system handles about 90,000 deliveries a day. Submission must be acknowledged in under 500 ms, so delivery is fully decoupled from it. Requirements: at least 3 attempts per task, one failure never affects another, and no file is ever silently lost.

**Decision Required:**

What buffers tasks between job submission and the worker tier?

| Decision                                                    | Rationale                                                                                                                                                                                                                           | Trade-offs                                                                                                                     |
| :---------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------- |
| **SQS standard queue with a dead-letter queue**             | Fully managed; absorbs a 2,000-message spike without tuning Visibility timeout \+ \`maxReceiveCount\` \= retry limit; DLQ \= no silent loss; per-message consumption \= task independence Priced per request; small at 90,000 a day | At-least-once delivery: duplicates will happen, so delivery must be idempotent AWS-specific; local development uses LocalStack |
| RabbitMQ (self-managed or Amazon MQ)                        | Richer routing; portable and easy to run locally                                                                                                                                                                                    | A broker to size, patch and make highly available; retries and dead-lettering configured by hand                               |
| Kafka (MSK)                                                 | Throughput headroom and replay                                                                                                                                                                                                      | A partitioned log is the wrong shape: one slow task blocks its partition; heaviest cost and operations                         |
| PostgreSQL as a queue (\`SELECT … FOR UPDATE SKIP LOCKED\`) | No new infrastructure; enqueue is transactional with job creation                                                                                                                                                                   | Couples delivery load to the primary database; retries, visibility and dead-lettering become our code                          |

**Decision:** SQS standard queue with a dead-letter queue

\- The one obligation SQS pushes onto us, idempotent delivery, must be solved under any reliable queue. It is handled by the conditional task claim and the platform's unique \`source_task_id\`.

\- FIFO queues rejected: ordering is not required, and per-group serialization works against independent fan-out.

**ADR-006: Service decomposition**

**Context:**

Scale is modest: 2,000 users, 200 concurrent. One boundary exists regardless of packaging: delivery workers call the document platform over HTTP.

**Decision Required:**

Is the backend one deployable (modular monolith) or several services, and how many?

| Decision                                                                         | Rationale                                                                                                                                                                                                                                                        | Trade-offs                                                                                                                                              |
| :------------------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Modular monolith: one deployable, three modules, one database with three schemas | One pipeline, one target group; authorization checks are in-process calls; cheapest                                                                                                                                                                              | Module boundaries are convention only; one failure and deploy domain The platform's API exists only for workers, so the product boundary is compromised |
| **Three services: auth, DocBridge API, document platform**                       | Ownership enforced with per-service credentials to isolated logical databases Independent deploys and failure domains; credential handling isolated in one small service The platform API is a real contract with two consumers (DocBridge API, delivery worker) | Three pipelines and services to run; membership checks become network calls on the latency path                                                         |

**Decision:** Three services: auth, DocBridge API, document platform

**ADR-007: Delivering task status updates to the client**

**Context:**

A job fans out into up to 2,000 tasks that resolve over seconds to minutes, and statuses must update automatically on the dashboard, the product's core screen. Up to 200 concurrent users, each typically watching one job.

**Decision Required:**

How does the browser receive task and job status updates while a job runs?

| Decision                                     | Rationale                                                                                                                                                           | Trade-offs                                                                                                                                             |
| :------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------ | :----------------------------------------------------------------------------------------------------------------------------------------------------- |
| Short polling every 2–3 seconds              | Simplest: one existing endpoint, no connection state; about 70 cheap reads/s at stated scale                                                                        | Updates lag by up to the interval; most requests return nothing new                                                                                    |
| Server-Sent Events                           | Fits the one-way traffic; native browser reconnect                                                                                                                  | API Gateway HTTP API buffers responses and caps connections at 30 s, so SSE needs a second public ingress that bypasses the gateway and breaks ADR-001 |
| **WebSockets via API Gateway WebSocket API** | Managed edge for persistent connections; services stay private, consistent with ADR-001 True push in near real time; auth on connect; push via \`PostToConnection\` | Bidirectional machinery for a one-way need; needs a connection registry and push plumbing Client must handle reconnect and catch-up itself             |

**Decision:** WebSockets via API Gateway WebSocket API

\- ‘connect’, disconnect' and \`subscribe\` handlers are .NET Lambdas (ADR-011); the JWT arrives as a query parameter and is validated against the JWKS.

\- A \`ws_connections\` table maps connections to the job they watch. The delivery worker pushes after each status write; \`410 Gone\` deletes the stale row; push failures never fail a delivery.

\- Catch-up: on subscribe and every reconnect, the client refetches \`GET /jobs/:id\` once, then applies pushes. No server-side replay buffer.

**ADR-008: Queue topology for job fan-out and task delivery**

**Context:**

Submission must be acknowledged in under 500 ms at p99 for jobs of up to 2,000 tasks. Inserting the job and task rows is one bulk transaction (tens of milliseconds), but SQS batches at most 10 messages per call, so enqueueing a full job takes about 200 calls, several seconds. Where enqueueing happens decides whether the latency target holds and whether a crash can strand tasks.

**Decision Required:**

Does the API enqueue task messages inline, or does a job-level message drive fan-out, and on a shared or separate queue?

| Decision                                                                                          | Rationale                                                                                                                                                                                                                     | Trade-offs                                                                                                 |
| :------------------------------------------------------------------------------------------------ | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------- |
| **Two queues: a job queue (one fan-out message per job) and a task queue (one message per task)** | Submission does constant work: one transaction, one message, return A crash mid-enqueue redelivers the job message, so fan-out resumes and nothing is lost Each queue gets its own visibility timeout, DLQ and redrive policy | A second queue and DLQ to run; a short window where tasks are pending rows without messages                |
| One shared queue carrying both message types                                                      | One queue                                                                                                                                                                                                                     | One timeout and DLQ for two workloads; fan-out messages wait behind thousands of deliveries; saves no cost |
| No job queue: the API enqueues every task inline                                                  | No fan-out consumer                                                                                                                                                                                                           | About 200 batch sends breaks the 500 ms target; a crash strands the rest with nothing to retry it          |

**Decision:** Two queues: a job queue (one fan-out message per job) and a task queue (one message per task)

\- \`POST /jobs\` writes the job and all task rows in one transaction, publishes one job message, and returns 201\. The dashboard shows every task as pending immediately.

\- If the publish fails after commit, the API still returns 201; the EventBridge sweeper (every 15 minutes) republishes stale jobs.

\- Visibility timeouts: job queue 2 minutes; task queue 6× the delivery function timeout.

**ADR-005: Database topology across services**

**Context:**

Each of the three services (ADR-006) owns clear data: auth (users, service clients), platform (teams, memberships, binders, folders, documents), DocBridge (files, jobs, tasks, WebSocket connections). The workload of 2,000 users and 90,000 tasks a day fits comfortably in one small instance.

**Decision Required:**

Do the services share one schema, share one instance with isolated logical databases, or each run their own instance?

| Decision                                                                        | Rationale                                                                                                                                                                                                                   | Trade-offs                                                                                                            |
| :------------------------------------------------------------------------------ | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :-------------------------------------------------------------------------------------------------------------------- |
| One instance, one shared schema                                                 | Cheapest; cross-service queries are one join away                                                                                                                                                                           | The first cross-service join couples services; no enforced ownership; splitting later is the most expensive migration |
| **One instance, three isolated logical databases with per-service credentials** | Each service's credentials reach only its own database, so cross-service joins are impossible by construction One instance to run and back up; promoting a database to its own instance later is a connection-string change | Cross-service reads become API calls; the instance is a shared failure domain and resource pool                       |
| Three separate instances                                                        | Strongest isolation; the textbook end-state                                                                                                                                                                                 | Three times the cost and operations for a workload that fits in one                                                   |

**Decision:** one PostgreSQL instance with \`auth\`, \`platform\` and \`docbridge\` databases.

\- Cross-service references are plain UUIDs with no cross-database foreign keys, validated through service APIs (for example, DocBridge asks the platform about membership).

\- Databases and roles are created by a bootstrap job; each service runs its own migrations from its own folder.

**ADR-010: Database engine per service**

**Context:**

With isolated logical databases, each service could use a different engine. Workloads: auth is small and strongly relational; the platform stores an arbitrarily deep folder hierarchy plus memberships; DocBridge inserts a job and up to 2,000 tasks atomically and aggregates task counts by status for the dashboard.

**Decision Required:**

Which database engine does each service use?

| Decision                                          | Rationale                                                                                                                                                                                                                               | Trade-offs                                                                                                                                                                                           |
| :------------------------------------------------ | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **PostgreSQL for all three**                      | \+ Every workload fits: nested folders as an indexed adjacency list, expanded one level at a time; atomic job \+ 2,000-task insert in one transaction; status counts as one \`GROUP BY\` \+ One engine to operate, tune and know deeply | − At extreme scale a row-per-task model could outgrow one relational instance                                                                                                                        |
| DynamoDB for jobs and tasks, PostgreSQL elsewhere | \+ Effectively unlimited write throughput                                                                                                                                                                                               | − Breaks submission atomicity: a job plus 2,000 tasks exceeds DynamoDB's 100-item transaction limit − Status aggregation becomes hand-built counters; 90,000 tasks a day is about 1 write per second |
| Document database for the platform hierarchy      | \+Nested structures feel native as documents                                                                                                                                                                                            | − Expand-one-level and subtree moves are simpler in an adjacency list; loses cross-entity transactions; a second engine to run                                                                       |

**Decision:** PostgreSQL for all three

\- Job status is derived from task rows on read, not stored; no counters to keep consistent under concurrent updates.

\- File bytes never go in a database; only metadata and checksums live in PostgreSQL.

**ADR-011: Worker compute model**

**Context:**

Workers consume SQS messages: fan-out (batch-send task messages) and delivery (fetch the staged file, verify its checksum, send it to the platform, update status, push a WebSocket update). Load is 90,000 tasks a day in bursts of up to 2,000; files average 2 MB, maximum 1 GB. Status writes go to PostgreSQL. Workers are written in C\# (ADR-017).

**Decision Required:**

What compute runs the queue consumers: Lambda, an ECS service, or EC2?

| Decision                                                         | Rationale                                                                                                                                                                            | Trade-offs                                                                                                                                                                                                                                                            |
| :--------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Lambda (.NET managed runtime) with SQS event source mappings** | \+ Scales to zero between bursts; per-message concurrency with no autoscaling config \+ Retry and DLQ semantics come with the SQS integration; fan-out and delivery sized separately | \- 15-minute cap: a 1 GB file must stream within it (it does, with margin) − Concurrent invocations can exhaust PostgreSQL connections, so RDS Proxy is required − .NET cold starts are slower than Node.js; occasional added latency on the first message of a burst |
| ECS Fargate worker service long-polling SQS                      | \+ No time cap; persistent connection pools; same container model as the APIs, so .NET code reuse is natural                                                                         | − Always-on cost for a workload idle most of the day; queue-depth autoscaling built and tuned by hand                                                                                                                                                                 |
| EC2 auto-scaling group                                           | \+Cheapest per hour at sustained load                                                                                                                                                | − Slowest scaling; instances to patch; sustained load is not this workload                                                                                                                                                                                            |

**Decision:** two .NET Lambda functions, fan-out (job queue) and delivery (task queue), plus small WebSocket handlers (ADR-007).

\- RDS Proxy fronts PostgreSQL; each function keeps a small Npgsql pool so warm instances reuse connections.

\- Delivery streams S3 GET → incremental SHA-256 → platform \`POST /documents\` in one pass, never buffering 1 GB in memory. Memory is sized for network throughput.

\- Delivery reserved concurrency starts at 50 (tunable in Terraform) to protect the RDS Proxy pool.

\- Cold starts are accepted: deliveries are asynchronous, so a slower first invocation costs no user-facing latency. SnapStart or Native AOT are the mitigations if measurements ever show a need.

**ADR-012: Infrastructure as Code tooling**

**Context:**

Every AWS resource (VPC, RDS and RDS Proxy, ECS services, SQS, S3, API Gateway, Lambda) must be created and managed as code; the console is for reading only.

**Decision Required:**

Which tool defines and manages the AWS infrastructure?

| Decision                    | Rationale                                                                                                                                                                                                         | Trade-offs                                                                                                   |
| :-------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------- |
| **Terraform (HCL)**         | \+ Industry default; declarative HCL maps one-to-one onto AWS resources, so the AWS building blocks stay visible \+ Explicit, inspectable state; plan shows every change before apply; skill transfers beyond AWS | \- A second language beside C\# and TypeScript; the state backend needs a one-time bootstrap                 |
| AWS CDK (C\# or TypeScript) | \+ Same language as the application; higher-level constructs cut boilerplate                                                                                                                                      | − Constructs hide the resources they create; compiles to CloudFormation with slower deploys and harder diffs |
| CloudFormation (raw YAML)   | \+Native, no state to manage                                                                                                                                                                                      | − Slow feedback and painful rollback on failed stacks                                                        |

**Decision:** Terraform (HCL)

\- Plan-then-apply forces every infrastructure change to be read and confirmed, and HCL teaches AWS itself rather than a wrapper. AI assistance removes most of HCL's verbosity cost.

\- Reusable modules live in \`infra/modules\`; \`infra/envs/dev\` and \`infra/envs/prod\` only call them with different variables.

\- The state backend (S3 bucket \+ DynamoDB lock table) is created by a script in \`bootstrap/\`, the one manual step.

**ADR-013: Worker authentication to the document platform**

**Context:**

Delivery workers call the platform's \`POST /documents\` asynchronously, possibly minutes after submission. The user's 15-minute JWT may have expired by then, and putting user tokens in queue messages would spread bearer credentials through SQS, logs and retries. Workers need their own identity, while the platform must still check that the submitting user is a member of the destination team at the time of delivery.

**Decision Required:**

How do delivery workers authenticate to the document platform?

| Decision                                                                                    | Rationale                                                                                                                                                                                                     | Trade-offs                                                                                                                 |
| :------------------------------------------------------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | :------------------------------------------------------------------------------------------------------------------------- |
| **Service token from the auth service (client credentials) plus an \`onBehalfOf\` user id** | \+ One mechanism system-wide: the platform validates every caller, user or service, as a JWT against the same JWKS \+ Service identity (who is calling) is separate from user authorization (is this allowed) | − Auth becomes a runtime dependency of delivery; the worker secret must be stored and rotated                              |
| IAM-signed requests (SigV4)                                                                 | \+ No stored secret; the Lambda execution role is the identity                                                                                                                                                | − Two auth paths on the platform (JWT and SigV4); SigV4 verification is custom code; breaks the local Docker Compose setup |
| Mutual TLS                                                                                  | \+Strong transport-level identity                                                                                                                                                                             | − Certificate issuance and rotation out of proportion to one internal edge; \`onBehalfOf\` still needed                    |
| Static shared API key                                                                       | Trivial to build                                                                                                                                                                                              | Long-lived bearer secret with no expiry, scope or subject                                                                  |

**Decision:** Service token from the auth service (client credentials) plus an \`onBehalfOf\` user id

\- Token scope is \`documents:ingest\`, claim \`token_use: 'service'\`, 15-minute expiry. The worker's client secret is in the Secrets Manager.

\- The token is cached in a static field across warm Lambda invocations and refreshed on expiry.

\- The platform re-checks \`onBehalfOf\` membership at ingest; a revoked membership fails the task with \`NOT_AUTHORIZED_AT_DELIVERY\`, not retried.

\- An auth outage is a transient failure, handled by the existing retry and DLQ path. Local Compose runs the same flow against the local auth container.

**ADR-014: Database access layer for services**

**Context:**

The three services and both Lambda workers are C\# (ADR-017) and talk to PostgreSQL through Npgsql. Four queries carry the system's correctness and must be written exactly as SQL intends:

\- the conditional task claim, \`UPDATE … WHERE status IN (…) RETURNING \*\`  
\- the job \+ up-to-2,000-task insert in one transaction, inside the 500 ms budget  
\- the idempotent ingest, \`INSERT … ON CONFLICT (source_task_id) DO NOTHING\`  
\- the job list's single aggregate query with \`count(\*) FILTER (WHERE …)\`

The schema also needs a pair of partial unique indexes on \`tasks\`.

**Decision Required:**

What library do services and workers use to define the schema, run migrations and execute SQL?

| Decision                                                                          | Rationale                                                                                                                                                                                                               | Trade-offs                                                                                                                                                                                 |
| :-------------------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Entity Framework Core only                                                        | \+ Typed models, LINQ queries and \`dotnet ef\` migrations in one tool; partial indexes via \`HasFilter\`                                                                                                               | − No native \`ON CONFLICT DO NOTHING\` or \`UPDATE … RETURNING\`; those drop to raw SQL anyway − Change tracking is overhead on the bulk task insert and in short-lived Lambda invocations |
| **EF Core for schema, migrations and CRUD; Dapper for the four critical queries** | \+ Migrations and everyday CRUD stay typed; the critical queries are hand-written SQL that runs exactly as written \+ Both run on Npgsql, so one connection string and one pool; composes with RDS Proxy without tuning | − Two ways to query; schema changes must be checked against the hand-written SQL                                                                                                           |
| Dapper \+ a SQL migration tool (DbUp or similar)                                  | \+Thinnest layer, full control of every statement                                                                                                                                                                       | − No model-to-schema link: a renamed column is caught only when a query runs                                                                                                               |
| Raw Npgsql commands                                                               | No dependencies                                                                                                                                                                                                         | − Hand-mapping every result row; most boilerplate                                                                                                                                          |

**Decision:** EF Core for schema, migrations and CRUD; Dapper for the four critical queries

\- Each service owns one \`DbContext\` and its migrations folder (ADR-009); migrations run as a deploy step, forward-only.

\- The four critical queries live in one repository class per service, each covered by an integration test against PostgreSQL in Docker Compose.

\- Lambdas use a small Npgsql pool (\`Maximum Pool Size\` kept low) behind RDS Proxy.

**ADR-015: Repository and build orchestration**

**Context:**

One repository holds a React \+ Vite SPA (TypeScript, npm), three ASP.NET Core services, two .NET Lambda workers plus WebSocket handlers, shared backend plumbing (JWT validation setup, error envelope), and Terraform. Two toolchains, \`dotnet\` and \`npm\`, must build, test and package it all in CI with per-service path filters.

**Decision Required:**

How are the backend projects organized and built, and what orchestrates builds across the repository?

| Decision                                                                                            | Rationale                                                                                                                                                                                                                                                                                                                                                | Trade-offs                                                                                                        |
| :-------------------------------------------------------------------------------------------------- | :------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------- |
| **One repository, one .NET solution for all backend projects, npm for \`web/\`, explicit CI steps** | \+ \`dotnet build\` and \`dotnet test\` already resolve project references in dependency order; nothing extra to learn \+ One place for shared build settings (\`Directory.Build.props\`) and package versions (\`Directory.Packages.props\`) \+ Each service's Dockerfile builds from the repo root and copies only its own project plus the shared one | − No build caching; every CI run of a service rebuilds it in full − Two toolchains wired by hand in each workflow |
| One solution per service                                                                            | \+ Each service builds in total isolation                                                                                                                                                                                                                                                                                                                | − The shared project is referenced from several solutions; cross-service refactors span several builds            |
| Separate repository per service                                                                     | \+Complete isolation                                                                                                                                                                                                                                                                                                                                     | − Shared code becomes a versioned NuGet package; one change spanning API and worker becomes several PRs           |

**Decision:** One repository, one .NET solution for all backend projects, npm for \`web/\`, explicit CI steps

\- At this size a task runner buys seconds of build time for a permanent layer of indirection. GitHub Actions path filters (\`services/auth/\*\*\`) already limit what each workflow builds.

\- The shared class library is deliberately thin: JWT validation setup, the error envelope and options binding. No domain models or data access, so each service's data and logic stay inside its own boundary.

\- Services: \`dotnet publish\` → Docker image tagged with the git SHA → ECR → ECS. Workers: \`dotnet publish\` → zip → Lambda function code update.

**ADR-016: Fan-out mechanism between job submission and task delivery**

**Context:**

ADR-008 set up a job queue and a task queue. A fan-out worker reads the job message, loads the task IDs already written by the submission transaction, and sends one message per task.

**Decision Required:**

Do task messages reach the task queue through a publish/subscribe layer (SNS or EventBridge), or does the fan-out worker send them to SQS directly?

| Decision                                                  | Rationale                                                                                                                                                                                | Trade-offs                                                                                                                  |
| :-------------------------------------------------------- | :--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :-------------------------------------------------------------------------------------------------------------------------- |
| **Fan-out worker batch-sends directly to the task queue** | \+ Fewest moving parts: one producer, one queue, one consumer \+ \`SendMessageBatch\` reports per-message success or failure, so a partial failure throws and the job message is retried | − Adding a second consumer later means inserting a topic and repointing the producer                                        |
| SNS topic between fan-out and the task queue              | \+ New consumers are new subscriptions; filter policies could route task classes                                                                                                         | − Only one consumer exists, so this buys optionality only; adds an independently failing hop; loses the batch result signal |
| EventBridge bus routing job events to the task queue      | \+Content-based routing if task events later feed other systems                                                                                                                          | − Same objection as SNS, with more configuration, cost and latency                                                          |
| SNS as the fan-out itself, replacing the worker           |                                                                                                                                                                                          | \- Does not work: SNS copies one message to many subscribers; it cannot expand one job into 2,000 distinct task payloads    |

**Decision:** Fan-out worker batch-sends directly to the task queue

\- Message body is \`{taskId}\` only; no tokens or file data in queues.

\- Fan-out is idempotent by construction: a redelivered job message re-sends task messages, and the delivery worker's conditional claim absorbs the duplicates.

**ADR-017: Backend language and runtime**

**Context:**

The Technical Design was written assuming Node.js/TypeScript for the whole stack: a TypeScript shared package, Drizzle for data access, esbuild-bundled Lambdas. The architecture itself (ADR-001 to ADR-016) is language-neutral. The SPA is React \+ TypeScript regardless. The backend covers three HTTP services, two queue workers and WebSocket handlers.

**Decision Required:**

Which language and runtime do the backend services and workers use?

| Decision                     | Rationale                                                                                                                                                                                                                                                                                 | Trade-offs                                                                                                                                                                                                  |
| :--------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | :---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Node.js / TypeScript         | \+ One language from SPA to workers; request and response types shared directly with the frontend \+ Fast Lambda cold starts; the design's library choices (Drizzle, npm workspaces) apply as written                                                                                     | − Type safety stops at runtime boundaries unless validated; less existing experience on the team                                                                                                            |
| **.NET (C\#, ASP.NET Core)** | \+ Builds on existing ASP.NET Core experience \+ Mature built-ins for this design: JWT bearer validation against a JWKS, Npgsql and EF Core, the AWS SDK for .NET, a managed Lambda runtime \+ Strong static typing across services and workers, including the concurrency-sensitive code | − Two languages in one repo; API types are no longer shared with the SPA − Slower Lambda cold starts than Node.js (accepted in ADR-011) − Node-specific parts of the Technical Design need .NET equivalents |

**Decision:** .NET (C\#, ASP.NET Core)

\- .NET for all backend services and workers; the SPA stays React \+ TypeScript.

\- The API contracts in the Technical Design remain the source of truth for SPA types; integration tests exercise each endpoint's request and response shape to catch drift.
