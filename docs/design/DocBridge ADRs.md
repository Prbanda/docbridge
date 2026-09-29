# ADR-001: Edge architecture for public API traffic

# ADR-001: Edge architecture for public API traffic

## Context

DocBridge exposes an HTTP API used by a browser SPA. Behind it sit multiple services (auth service, DocBridge API, document platform), with more likely later. Every endpoint must reject unauthenticated requests. Services run as containers on ECS inside a VPC.

## Decision required

**How do we route public API traffic from the internet to our internal services?**

## Alternatives evaluated

| 1 | Single public ALB, path/host rules to one target group per service |  |
| :---: | :---- | :---- |
|  | \+ Simplest correct setup, one routing layer to configure and debug \+ Native fit for ECS: target groups, health checks, per-service scaling \+ Cheapest at our traffic level | \- Services sit behind a public-facing entry point; no network boundary between the internet and the ALB \- JWT validation happens inside each service, no edge rejection \- No managed throttling; rate limiting must be built in the application |
| 2 | **API Gateway (HTTP API) as public edge → VPC Link → internal ALB → services** |  |
|  | \+ JWTs validated at the edge by a native JWT authorizer; invalid traffic never enters the private network \+ Managed throttling at the edge \+ Services and ALB live entirely in private subnets with zero public exposure | \- Two routing layers make debugging harder \- VPC Link and authorizer add configuration surface \- Additional per-request cost over ALB alone |
| 3 | API Gateway only, direct integrations to services |  |
|  | \+ One managed edge with auth and throttling \+ No ALB to run | \- HTTP API still needs a VPC Link and a load balancer or Cloud Map to reach ECS \- Loses ALB-style per-service target groups and health-check-driven routing |

## Decision

* Option 2: API Gateway (HTTP API) as the public edge, internal ALB for service routing.  
    
  * Unauthenticated traffic is rejected before it enters the VPC, services have no public exposure, and the design exercises both edge security and load-balancer concepts, which is an explicit goal of this project. HTTP API (not REST API) is specified because it has a native JWT authorizer and native VPC Link at roughly a third of REST API cost.  
  * Login and registration routes are configured without the JWT authorizer, since they are the routes that issue tokens.  
  * Rollout note: deploy services \+ internal ALB first and verify from inside the VPC, then add API Gateway and the JWT authorizer in front, so the layering is understood rather than inherited.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-002: File transfer path between browser and s…

# ADR-002: File transfer path between browser and staging store

## Context

Users upload up to 100 files per session (average 2 MB, maximum 1 GB) and team members can later download staged files. Requirements state that the UI must never freeze during upload, and file integrity must be verifiable end to end. Job submission must acknowledge in under 500 ms regardless of batch size. Files are encrypted at rest via KMS-managed server-side encryption (ADR-003), so nothing key-related needs to travel with the file bytes.

## Decision required

**Do file bytes move between the browser and the staging store directly (presigned URLs), or through our API servers?**

## Alternatives evaluated

| 1 | Presigned transfers: presigned POST for uploads, presigned GET for downloads, both scoped to a single object with short expiry |  |
| :---: | :---- | :---- |
|  | \+ API tier scales with request volume, not upload bandwidth; a 1 GB transfer never occupies an API connection \+ Presigned POST policies enforce a hard size ceiling (content-length-range) and content type at the storage layer \+ Client-supplied SHA-256 is verified by S3 on write and re-verified by workers before delivery, giving end-to-end integrity \+ Parallel browser-to-S3 uploads keep the UI responsive with per-file progress | \- Upload completion is not observed in-band; the API must verify object existence and checksum before delivery instead of trusting the client \- URL issuance is an extra request per file \- A presigned URL is a bearer credential while valid (mitigated by single-object scope and short expiry, ADR-004) |
| 2 | Proxy all file bytes through the API servers |  |
|  | \+ Upload success and failure are observed directly by the API \+ Single point for validation and virus/type scanning \+ No bearer-credential URLs exist at all | \- API tier must scale with aggregate file bandwidth; slow 1 GB uploads hold connections open for minutes \- Every byte transits twice (browser → API → S3), doubling transfer cost and latency \- Resumable upload logic must be built by hand |
| 3 | Hybrid: proxy uploads, presigned downloads |  |
|  | \+ In-band upload observation where it matters most \+ Downloads still bypass servers, meeting the requirement | \- Keeps all the upload-side scaling and connection problems of Option 2 \- Two transfer mechanisms to build, secure, and test instead of one |

## Decision

* Option 1: Presigned transfers in both directions.  
    
  * The API stays out of the byte path entirely. Every presigned URL is issued only after an authorization check (team membership for downloads, session ownership for uploads), is scoped to exactly one object key, and expires on the schedule set in ADR-004. Uploads use presigned POST rather than presigned PUT specifically because POST policies can enforce a maximum object size, which PUT cannot.  
  * Workers confirm the object exists and its checksum matches the recorded SHA-256 before any delivery. A missing or mismatched object fails the task with a clear reason (never silent).  
  * Files above the multipart threshold (around 100 MB) upload via S3 multipart with individually signed part requests, which also gives resume-from-part on interrupted uploads.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-003: Encryption at rest for staged files

# ADR-003: Encryption at rest for staged files

## Context

All staged files must be encrypted at rest. The requirements mandate that encryption be managed by a dedicated cloud key management service and explicitly forbid implementing our own cryptography. Files reach the staging store directly from the browser via presigned URLs (ADR-002), so the encryption mechanism must not require sensitive key material on the client. Staged files contain clinical trial documents, so key control (rotation, revocation, audit) matters.

## Decision required

**Which S3 server-side encryption mode do we use for the staging store?**

## Alternatives evaluated

| 1 | SSE-S3 (S3-managed keys) |  |
| :---: | :---- | :---- |
|  | \+ Zero configuration, no per-request KMS cost \+ Fully compatible with presigned transfers | \- No key we control: no custom key policy, no revocation, no independent audit trail of key usage \- Weakest for a compliance-sensitive domain: encrypted but not keys managed by us |
| 2 | **SSE-KMS with the AWS-managed key (aws/s3), enabled with one bucket setting** |  |
|  | \+ One toggle, no key to create or manage; presigned transfers work unchanged \+ CloudTrail entries for key usage; no monthly key fee \+ Satisfies the requirement verbatim: encryption managed by a dedicated cloud KMS, no crypto implemented by us | \- Key policy is fixed and unmodifiable: anyone in the account with S3 permissions can decrypt, so the key is not an access boundary \- No revocation: the key cannot be disabled or deleted, so there is no kill switch over staged data \- Rotation schedule is AWS's, not ours; audit without control |
| 3 | SSE-KMS with a customer-managed key (CMK) |  |
|  | \+ We own the key: policy, rotation schedule, revocation; decryption becomes an independent authorization check (key policy scoped to the URL-issuing API role and worker role) \+ Key material never leaves KMS; presigned uploads and downloads work unchanged | \- Setup: key resource, key policy \- Per-request KMS charges (reduced by S3 Bucket Keys) \- KMS availability dependency, same as Option 2 |
| 4 | SSE-C (customer-provided keys, raw key material stored in our metadata store) |  |
|  | \+ Full possession of key material, independent of KMS | \- Key bytes must be sent with every S3 request; using presigned URLs would hand raw keys to the browser, which is unacceptable \- We would be storing and protecting raw key material ourselves \- Lose S3-side key rotation, audit, and revocation entirely |

## Decision

* Option 2: SSE-KMS with the AWS-managed aws/s3 key, with S3 Bucket Keys enabled to cut KMS request cost.  
    
  * To satisfy the requirement: "encryption managed by a dedicated cloud key management service, no cryptography implemented by us": KMS performs the encryption, usage is audited in CloudTrail, and we write no crypto. Access control is enforced where it already lives: IAM, the bucket policy, and authorized presigned URL issuance.  
  * Option 3 (CMK) is the documented upgrade path, triggered by a client or auditor requiring customer control of the key. The upgrade is a bucket-setting change plus one key resource, invisible to application code, and because the staging bucket self-purges on the 30-day lifecycle, the entire bucket is re-keyed within a month of the flip with zero migration work.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-004: Presigned URL expiry policy

# ADR-004: Presigned URL expiry policy

## Context

A presigned URL is a bearer credential: anyone holding it can perform the signed operation until it expires, with no further auth check. Expiry is therefore the primary control on exposure. The two directions have different usage patterns: an upload URL is requested and used immediately within an active session, but must tolerate large files (up to 1 GB) on slow connections; a download URL is clicked immediately after being issued. S3 validates the signature when the request starts, so a transfer that begins before expiry completes if expiry passes mid-transfer.

## Decision required

**How long do presigned upload (POST) and download (GET) URLs remain valid?**

## Alternatives evaluated

| 1 | Long expiry for both (hours-scale, \~12h) |  |
| :---: | :---- | :---- |
|  | \+ User can walk away mid-session and resume without re-requesting URLs \+ Fewest URL issuance requests | \- A leaked URL grants access to clinical trial material for hours \- Contradicts the requirement that files are accessible only to authorized team members at every point: a long-lived URL is a standing bypass of that check |
| 2 | **Short, asymmetric expiry: 15 minutes for uploads, 2 minutes for downloads** |  |
|  | \+ Exposure window is minutes, a download URL is single-use in practice \+ 15 minutes comfortably covers time-to-start for uploads even on batches of 100 files requested up front (S3 only validates at request start, so the 1 GB transfer itself can outlive the expiry) \+ Downloads are clicked immediately, uploads are queued by the client and started within the window | \- An interrupted session needs fresh URLs (one cheap authorized API call) \- Client must request upload URLs in waves rather than assuming indefinite validity |
| 3 | Very short for both (under 1 minute) |  |
|  | \+ Minimal exposure window | \- Breaks batch uploads: the browser cannot start 100 uploads within 60 seconds of issuance, forcing constant re-issuance churn |

## Decision

* Option 2: 15-minute upload URLs, 2-minute download URLs.  
    
  * Asymmetry based on usage: downloads are consumed instantly so they get the tightest practical window; uploads need slack for browsers queuing large batches, and 15 minutes provides it without meaningful added risk. Both values are configuration, so they can be adjusted from evidence without a redesign.  
  * Every URL remains scoped to exactly one object key and one operation; expiry is the second layer, not the only one.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-005: Task queue technology for asynchronous d…

# ADR-005: Task queue technology for asynchronous delivery

## Context

One submission fans out into up to 2,000 independent tasks, and the system handles 90,000 deliveries per day. Submission must acknowledge in under 500 ms, so task execution is fully decoupled from submission. At least 3 delivery attempts before a task is marked failed, complete task independence (one failure never affects another), and no silent loss.

## Decision required

**What buffers tasks between job submission and the worker tier?**

## Alternatives evaluated

| 1 | SQS standard queue with a dead-letter queue |  |
| :---: | :---- | :---- |
|  | \+ Fully managed: no brokers to run, patch, or scale; absorbs a 2,000-message spike without tuning \+ Retry semantics match the requirement almost verbatim: visibility timeout returns unacknowledged messages to the queue, maxReceiveCount enforces the attempt limit, and the DLQ catches exhausted tasks so nothing is ever silently lost \+ Per-message consumption is exactly the per-task independence model \+ We pay per request; at 90k/day the cost is small | \- At-least-once delivery: workers must treat task delivery as idempotent because duplicates will occur \- AWS-specific; local development uses an emulator (e.g. LocalStack) or a real dev queue |
| 2 | RabbitMQ (self-managed or Amazon MQ) |  |
|  | \+ Richer routing (topics, priorities) if task classes diverge later \+ Portable across clouds and trivially runnable locally | \- A broker to size, patch, monitor, and make highly available, all orthogonal to what this system needs \- Retries and dead-lettering must be configured by hand to reach what SQS gives by default \- Amazon MQ instances cost more at this volume than per-request SQS |
| 3 | Kafka (MSK) |  |
|  | \+ Extreme throughput headroom and event replay \+ Natural fit if task events later feed analytics or audit streams | \- Partition-ordered log is the wrong shape for independent work items: one slow task blocks its partition, conflicting with task independence \- Per-message retry/DLQ semantics must be built in application code \- Heaviest operational and cost footprint of all options, unjustified at 90k msg/day |
| 4 | PostgreSQL as a queue (jobs table with SELECT ... FOR UPDATE SKIP LOCKED) |  |
|  | \+ No new infrastructure; task state and queue live in one place, so enqueueing is transactional with job creation \+ Simple to reason about and inspect with SQL | \- Couples delivery throughput to the primary database; a worker-side spike contends with user-facing queries \- Retry timers, visibility, and dead-lettering all become application code, which is exactly the machinery a managed queue exists to provide \- Polling load grows with worker count |

## Decision

* Option 1: SQS standard queue with a dead-letter queue.  
    
  * Its semantics map one-to-one onto the requirements: visibility timeout plus maxReceiveCount implements "at least 3 attempts", per-message consumption implements task independence, and the DLQ implements "no silent loss" as an infrastructure guarantee rather than a code promise. The one obligation it pushes onto us, idempotent delivery, is already a listed risk in the blueprint and must be handled regardless of queue choice, since any reliable queue is at-least-once in practice.  
  * FIFO queues were considered and rejected inside Option 1: ordering is explicitly not required, and FIFO's throughput ceiling and per-message-group serialization work against independent fan-out.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-006: Service decomposition

# ADR-006: Service decomposition

## Context

Scale is modest (2,000 users, 200 concurrent). One constraint is structural: delivery workers call the platform over HTTP regardless of how the API tier is packaged, so at least one real service boundary exists no matter what.

## Decision required

**Is the API tier one deployable (modular monolith) or multiple services, and if multiple, how many?**

## Alternatives evaluated

| 1 | Modular monolith: one deployable, three internal modules, one database with three schemas |  |
| :---: | :---- | :---- |
|  | \+ Simplest operations: one pipeline, one target group, one thing to monitor \+ Authorization checks are in-process calls, no network hop on the browse/submit/download path \+ Cross-module transactions possible \+ Cheapest | \- Module boundaries are convention \- One failure and deploy domain: a bad platform deploy takes login down \- The platform's "API" becomes internal calls; the required external contract exists only for workers, so the product boundary is compromised |
| 2 | **Three services: auth, DocBridge API, document platform** |  |
|  | \+ Ownership enforced end to end: per-service credentials to isolated logical databases \+ Independent deploys and failure domains; auth stays up when platform deploys \+ The platform API is a genuine contract with two real consumers (DocBridge API and delivery workers) \+ Passwords and credentials isolated in one small service | \- 3 pipelines, 3 target groups, 3 services to size and monitor \- Membership checks become API calls on the browse/submit/download latency path \- Distributed failure modes (platform down blocks submission validation) |
| 3 | Two services: auth merged into DocBridge API, platform separate |  |
|  | \+ Preserves the one boundary the requirements force (platform) \+ One fewer deployable than Option 2 | \- Credential handling entangled with application logic in the largest, most frequently deployed service \- Auth is the cheapest service to isolate (tiny, stable, rarely deployed) and the most valuable: smallest possible surface touching passwords \- Saves little: the ALB, gateway, and DB topology already exist for Option 2 |

## Decision

* Option 2: 3 services.  
    
  * The platform boundary is forced by the requirements; the auth boundary is nearly free and isolates credential handling. The accepted cost is explicit: authorization is a service call, and the platform service sits on the latency path of the 1-second destination browse target.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-007: Delivering task status updates to the cl…

# ADR-007: Delivering task status updates to the client

## Context

A job fans out into up to 2,000 tasks that resolve over seconds to minutes. The requirements state that statuses "update automatically as work progresses"; the mechanism is ours to choose. The job dashboard is the product's core screen, so update latency is user-visible quality. Scale is modest: up to 200 concurrent users, each typically watching one job.

## Decision required

**How does the browser receive task and job status updates while a job is running?**

## Alternatives evaluated

| 1 | Short polling (client refetches job status every 2–3 seconds) |  |
| :---: | :---- | :---- |
|  | \+ Simplest possible implementation: one existing endpoint, no connection state anywhere \+ Works through the existing edge unchanged \+ At 200 concurrent users this is \~70 req/s of cheap indexed reads; load is a non-issue at stated scale | \- Updates lag by up to the polling interval; the dashboard feels stepped, not live \- Most requests return nothing new; chattiest option per unit of information |
| 2 | Server-Sent Events (SSE) |  |
|  | \+ Natural fit for the traffic shape: strictly one-way, server-to-client status stream \+ Native browser support with automatic reconnect and Last-Event-ID resume \+ Minimal client and server code in isolation | \- Incompatible with our edge: API Gateway HTTP API buffers responses and caps connections at 30 s, so SSE requires a second, public-facing ingress (public ALB) that bypasses the gateway \- The second ingress breaks the ADR-001 posture (zero public service exposure, JWT enforced at one edge) and splits auth enforcement across two paths |
| 3 | **WebSockets via API Gateway WebSocket API** |  |
|  | \+ Purpose-built managed edge for persistent connections; services stay fully private, consistent with ADR-001 \+ True push: status changes reach the dashboard in near real time \+ Connection auth on connect, managed connection lifecycle, and push via the post-to-connection API; no sockets terminated on our containers | \- Bidirectional machinery for a one-way need \- Requires a connection registry (which user/job each connection watches) and push plumbing from the status-update path to the gateway \- A second gateway deployment to configure and monitor \- Client must handle reconnect and missed-update catch-up itself |

## Decision

* Option 3: WebSockets via API Gateway WebSocket API.  
    
  * At this system's scale, all 3 options function; the decision is driven by architectural consistency and product quality. WebSocket API delivers real-time push while keeping every service private and all client entry points managed at the edge.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-008: Queue topology for job fan-out and task …

# ADR-008: Queue topology for job fan-out and task delivery

## Context

Submitting a job creates up to 2,000 tasks and must acknowledge in under 500 ms at p99. Inserting the job and task rows is a single bulk transaction (10s of milliseconds), but enqueueing task messages is not: SQS batches at most 10 messages per call, so a full job means roughly 200 send calls, which takes seconds. Where and when task messages get enqueued therefore determines whether the latency target survives large jobs, and whether a crash mid-enqueue can strand tasks. Delivery itself is already decided: one SQS message per task, consumed by the worker tier (ADR-005).

## Decision required

**How do task messages get from job submission into the task queue: does the API enqueue them inline, or does a job-level message drive fan-out, and if so, on a shared or separate queue?**

## Alternatives evaluated

| 1 | Two queues: a job queue carrying one fan-out message per job, and the task queue carrying one message per task |  |
| :---: | :---- | :---- |
|  | \+ Submission does constant work regardless of batch size: one transaction, one message, return; the 500 ms target holds at 2,000 tasks \+ Fan-out inherits SQS retry semantics: a crash mid-enqueue redelivers the job message and fan-out resumes, so no task is ever silently lost \+ Each queue gets its own visibility timeout, DLQ, and redrive policy sized for its workload  \+ Fan-out and delivery backlogs are separate scaling signals | \- A second queue and DLQ to provision and watch \- A short window where tasks exist as pending rows before their messages exist in the task queue \- Fan-out must be idempotent on redelivery (harmless here: re-enqueued task messages are absorbed by per-task delivery idempotency) |
| 2 | One shared queue carrying both message types, workers branch on type |  |
|  | \+ One queue to provision \+ Same crash-safety for fan-out as Option 1 | \- One visibility timeout, DLQ, and redrive policy must serve two different workloads \- Fan-out messages queue behind thousands of delivery messages during bursts, delaying the step that feeds everything else \- Backlog depth becomes meaningless as a signal because it mixes 2 workloads \- Saves nothing in cost: SQS is priced per request, not per queue |
| 3 | No job queue: the API enqueues all task messages inline during submission |  |
|  | \+ Simplest, no fan-out consumer at all \+ No window between task creation and task message existence | \- Breaks the latency target: \~200 batch sends for a full job takes seconds, not 500ms \- A crash mid-enqueue strands the unenqueued remainder in pending with no message and no retry driver, violating "nothing silently lost" |

## Decision

* Option 1: Separate job queue and task queue.  
    
  * Submission writes the job and all task rows in one transaction, publishes a single job message, and returns. A fan-out consumer reads the job message, loads the task IDs, and batch-sends one message per task to the task queue. The fan-out step is retryable by the queue itself, keeping the no-silent-loss guarantee intact through enqueueing, not just delivery.  
  * Task rows are created in the submission transaction, not by the fan-out consumer: the dashboard shows the complete task list as pending the moment the ack returns, and fan-out reduces to a naturally idempotent enqueue of already-recorded work.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-009: Database topology across services

# ADR-009: Database topology across services

## Context

DocBridge consists of three services with clear data ownership: the auth service (users, credentials), the document platform (teams, memberships, binders, folders, documents), and the DocBridge API plus workers (jobs, tasks, file metadata). A stated goal is a genuine service architecture where each service owns its data. Scale is modest: 2,000 registered users, 90,000 tasks/day, comfortably within one small database instance. The decision is how to physically and logically arrange the services' data.

## Decision required

**Do the three services share one database schema, share one instance with isolated logical databases, or each run their own database instance?**

## Alternatives evaluated

| 1 | One instance, one shared schema, all services connect to the same database |  |
| :---: | :---- | :---- |
|  | \+ Cheapest and simplest to provision \+ Cross-service queries are trivial (one join away) | \- The first cross-service join couples services at the schema level, and every schema change becomes a multi-service negotiation \- No enforcement of ownership; any service can read or write any table \- Splitting later requires untangling accumulated joins which is the most expensive migration in microservices |
| 2 | **One instance, three isolated logical databases with per-service credentials** |  |
|  | \+ Ownership is enforced: each service's credentials can only reach its own database, so cross-service joins are impossible by construction \+ One instance to run, back up, and pay for, matching the actual scale \+ Promoting a logical database to its own instance later is just a connection-string change \+ Cross-service data needs are forced through service APIs | \- Cross-service reads (e.g. DocBridge checking team membership) become API calls with their own latency and failure modes \- The instance is a shared failure domain and a shared resource pool: a runaway query in one database can starve the others |
| 3 | 3 separate database instances, one per service |  |
|  | \+ Strongest isolation: independent failure domains, independent scaling, independent maintenance windows \+ The textbook microservice end-state | \- Three instances of cost, backups, patching, and monitoring for a workload that fits comfortably in one small instance \- Option 2 already preserves the ability to get here later if scale demands it |

## Decision

* Option 2: One PostgreSQL instance, 3 isolated logical databases (auth, platform, docbridge), each accessible only by its owning service's credentials.  
    
  * This keeps the enforced data ownership with no cross-service joins, while matching infrastructure spend to real scale. The known consequence is accepted explicitly: DocBridge authorizes destination access by asking the platform service over its API, not by joining to membership tables.  
  * The shared-instance failure domain is acceptable at this scale; if one service's load ever threatens the others, its logical database moves to its own instance without schema changes.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-010: Database engine per service

# ADR-010: Database engine per service

## Context

With one instance, 3 isolated logical databases, each service could use a different engine. The workloads: auth stores users and credentials (small, strongly relational); the document platform stores a nested hierarchy (teams → binders → folders, arbitrarily deep) plus memberships and document records; DocBridge stores jobs, tasks, and file metadata, with a burst-insert pattern (a job plus up to 2,000 task rows atomically) and dashboard queries that aggregate task counts by status per job.

## Decision required

**Which database engine does each service use, one engine for all or per-service choices?**

## Alternatives evaluated

| 1 | PostgreSQL for all three services |  |
| :---: | :---- | :---- |
|  | \+ Every workload fits: credentials are textbook relational; nested folders are a solved problem (parent reference \+ recursive CTEs) with an access pattern of one indexed level per expand; job submission is one transaction inserting a job and 2,000 tasks; status aggregation is a GROUP BY on an indexed column \+ One engine to operate, tune, back up, and know deeply \+ Transactional guarantees for atomic submission and consistent status \+ Compatible with the single-instance topology | \- At extreme scale the job/task write load could outgrow a relational row-per-task model \- Recursive hierarchy queries need deliberate indexing to stay fast at depth |
| 2 | DynamoDB for the job/task store, PostgreSQL elsewhere |  |
|  | \+ Effectively unlimited write throughput for task status updates \+ Pay-per-request pricing fits bursty fan-out | \- Submission atomicity breaks: a job plus 2,000 tasks exceeds DynamoDB transaction limits (100 items), so "job accepted" stops being one atomic fact \- Status aggregation must be hand-built with counters or streams, replacing one GROUP BY with distributed bookkeeping \- 90,000 tasks/day is \~1 write/second average; the throughput being bought is three orders of magnitude beyond the need |
| 3 | Document database (DocumentDB/Mongo) for the platform hierarchy, PostgreSQL elsewhere |  |
|  | \+ Nested structures feel native to model as documents | \- The access pattern is expand-one-level and move-a-subtree, which relational adjacency lists handle trivially; embedding the tree in documents makes moves and per-node permissions harder, not easier \- Loses cross-entity transactions (membership \+ hierarchy changes) \- Adds a second engine to operate for zero measured benefit |

## Decision

* Option 1: PostgreSQL for all three logical databases.  
    
  * Every stated workload is inside PostgreSQL's comfort zone, and the alternatives each trade a needed property (submission atomicity, one-query aggregation, simple subtree operations).  
  * File bytes are explicitly out of scope for any database: they live in the encrypted staging store (S3), with only metadata and checksums in PostgreSQL.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-011: Worker compute model

# ADR-011: Worker compute model

## Context

Workers consume SQS messages: fan-out (batch-send task messages) and delivery (fetch staged file, verify checksum, deliver to platform, update status, push WS update). Load: 90,000 tasks/day, bursty (2,000-task spikes), avg file 2 MB, max 1 GB. Status writes go to PostgreSQL.

## Decision required

**What compute runs the queue consumers: Lambda, an ECS service, or EC2?**

## Alternatives evaluated

| 1 | Lambda with SQS event source mappings |  |
| :---: | :---- | :---- |
|  | \+ Scales to zero between bursts; no idle cost at 90k/day \+ Per-message concurrency scaling with no autoscaling config \+ Retry \+ DLQ semantics native to the SQS integration \+ Fan-out and delivery are separate functions with separate sizing | \- 15-min hard cap; 1 GB worst case must stream within it (it does, with margin) \- Concurrent invocations exhaust Postgres connections: RDS Proxy becomes required \- No resident process: no in-memory batching; cold starts |
| 2 | ECS Fargate worker service, long-polling SQS |  |
|  | \+ No execution time cap; persistent DB connection pools, no proxy needed \+ Same container model as the API services \+ Resident process enables in-memory coalescing | \- Always-on cost for a bursty workload that is idle most of the day \- Queue-depth autoscaling must be built and tuned by hand |
| 3 | EC2 auto-scaling group of consumers |  |
|  | \+ Cheapest per compute-hour at sustained high load | \- Slowest scaling; instances to patch and manage \- Sustained high load is not this workload |

## Decision

* Option 1: Lambda, as two functions: fan-out (job queue) and delivery (task queue), plus small WS lifecycle handlers ($connect/$disconnect/subscribe).  
    
  * The workload is per-message independent, which is Lambda's exact shape. Accepted consequences, written into the design: RDS Proxy in front of PostgreSQL; delivery function sized (memory \= network throughput) for the 1 GB worst case.


* Ratified by:

  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-012: Infrastructure as Code tooling

# ADR-012: Infrastructure as Code tooling

## Context

All AWS resources for DocBridge (VPC, RDS, ECS services, SQS, S3, KMS, API Gateway, Lambdas) must be created and managed as code.

## Decision required

**Which tool defines and manages the AWS infrastructure?**

## Alternatives evaluated

| 1 | Terraform (HCL) |  |
| :---: | :---- | :---- |
|  | \+ Industry default for IaC \+ Declarative HCL maps 1:1 to AWS resources, so students learn the actual AWS building blocks, not an abstraction over them  \+ State is explicit and inspectable; drift detection built in  \+ Cloud-agnostic skill, transfers beyond AWS | \- A second language (HCL) next to TypeScript \- No native programming constructs; complex logic gets awkward (mitigated: this project needs none) \- State backend must be bootstrapped manually once |
| 2 | AWS CDK (TypeScript) |  |
|  | \+ Same language as the application stack; loops, types, and IDE support for free \+ Higher-level constructs cut boilerplate (e.g. one construct wires Lambda \+ SQS \+ DLQ) \+ Native AWS product with first-class support | \- Constructs hide the resources they create \- Compiles to CloudFormation: slower deploys, harder-to-read diffs, CFN limits inherited \- AWS-only skill |
| 3 | CloudFormation (raw YAML) |  |
|  | \+ Zero extra tooling, native to AWS \+ No state to manage | \- Slow feedback loop, painful rollback behavior on failed stacks  |

## Decision

* Option 1: Terraform.  
    
  * The tiebreaker is the learning goal plus resume value. Terraform's plan/apply loop forces us to read and confirm every infrastructure change, and HCL's 1:1 mapping helps to learn AWS itself, not a wrapper. AI assistance removes the historical cost of HCL verbosity, since boilerplate is no longer hand-written.  
  * The state backend (S3 bucket \+ DynamoDB lock table) is the one manually bootstrapped resource, created by a script in `bootstrap/` so even that step is reproducible.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-013: Worker authentication to the document pl…

# ADR-013: Worker authentication to the document platform

## Context

Delivery workers (Lambda) call the document platform's `POST /documents` endpoint to ingest files. Every platform endpoint must reject unauthenticated requests. On the read path, DocBridge forwards the end user's JWT, but workers run asynchronously, possibly minutes after submission: the user's 15-minute token may be expired, and holding user tokens in queue messages would spread bearer credentials through SQS, logs, and retries. Workers therefore need their own identity, while the platform still must verify that the original submitting user is authorized at the destination (the `onBehalfOf` check at time of use).

## Decision required

**How do delivery workers authenticate to the document platform API?**

## Alternatives evaluated

| 1 | Service token from the auth service (client-credentials style): the worker exchanges its own credential for a short-lived service JWT; requests carry the service JWT plus an onBehalfOf user id |  |
| :---: | :---- | :---- |
|  | \+ One auth mechanism system-wide: the platform validates every caller (user or service) against the same JWKS, one middleware path \+ Clear separation of concerns: the service token proves that this is the delivery worker, onBehalfOf names the user, and the platform re-checks that user's membership at ingest | \- The auth service becomes a runtime dependency of delivery; if auth is down, deliveries fail (transient path: retries \+ DLQ handle it) \- Worker credential must be stored (Secrets Manager) and rotated \- A few days of work: token endpoint, scope validation, caching the token across warm invocations |
| 2 | IAM-signed requests (SigV4): platform verifies the caller's IAM identity |  |
|  | \+ No credential to store; Lambda's execution role is the identity, rotation handled by AWS  \+ AWS-idiomatic, zero standing secrets | \- Splits the platform's auth into two mechanisms: JWT for users, SigV4 for services; two validation paths to build, test, and secure \- SigV4 verification on the platform side is nontrivial custom code (or forces the platform behind API Gateway internally, changing the architecture)  \- Couples the platform's auth model to AWS; breaks the local docker compose |
| 3 | mTLS between workers and the platform |  |
|  | \+ Strong mutual authentication at the transport layer, independent of application code | \- Certificate issuance, distribution, and rotation is real operational machinery, heavy for one internal edge  \- ALB/Lambda mTLS termination adds configuration surface out of proportion to the need  \- Identity is "holds the cert", coarser than scoped tokens; onBehalfOf still needs an application-layer mechanism anyway |
| 4 | Static shared API key in a header, stored in Secrets Manager |  |
|  | \+ Trivial to implement and test locally  \+ No new auth flows | \- A long-lived bearer secret with no expiry, no scopes, no subject; leak \= full ingest access until manually rotated  \- No standard validation or audit story; exactly the pattern the rest of the design avoids |

## Decision

* Option 1: Service token issued by the auth service.  
    
  * It keeps a single verification path on the platform (everything is a JWT against the same JWKS), gives the worker a scoped, short-lived, auditable identity, and cleanly separates service identity from user authorization: the platform trusts the token for "who is calling" and re-checks onBehalfOf membership for "is this allowed", which is the time-of-use authorization the design already requires.  
  * The auth-service dependency is acceptable because delivery already has a retry-and-DLQ path for transient failures, and auth is the smallest, most stable, least-deployed service in the system.  
  * Implementation notes: worker credential in Secrets Manager; token cached in the Lambda execution context across warm invocations and refreshed on expiry; token scope limited to documents:ingest; local compose uses the same flow against the local auth container, so dev and cloud behave identically.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-014: Database access layer for services

# ADR-014: Database access layer for services

## Context

All 3 services (auth, platform, DocBridge API) and both Lambda workers read and write PostgreSQL through their own logical database and credentials (ADR-009, ADR-010). The stack is Node.js/TypeScript throughout. Several queries in this system are not simple CRUD and depend on SQL semantics. Example: a single transaction inserting a job row plus up to 2,000 task rows sized to hold a 500 ms p99, a conflict-aware insert for ingest idempotency, an aggregate query across a page of jobs grouped by status, and a pair of partial unique indexes on the tasks table instead of one plain constraint. Access layer has to express all of this without fighting the abstraction, since these are exactly the queries the correctness requirements depend on.

## Decision required

**What library do the services and workers use to build and run SQL queries against PostgreSQL?**

## Alternatives evaluated

| 1 | Prisma |  |
| :---: | :---- | :---- |
|  | \+ Strong TypeScript inference generated from a schema file \+ Largest ecosystem, most widely adopted Node ORM \+ Built-in migration tooling | \- No native way to express a conditional UPDATE ... WHERE status IN (...) RETURNING \* as one atomic call; requires dropping to $queryRaw, forfeiting typing on exactly the query where correctness matters most \- Runs its own internal connection pool; placing that in front of RDS Proxy (already required for Lambda) needs tuning to avoid 2 pooling layers fighting each other \- The abstraction is heaviest: several of the system's queries would bypass the query builder entirely |
| 2 | **Drizzle ORM (drizzle-orm \+ drizzle-kit)** |  |
|  | \+ Schema defined in TypeScript, queries type-checked against it, but the generated SQL stays close enough to hand-written SQL that UPDATE ... RETURNING, ON CONFLICT DO NOTHING RETURNING, and multi-row batch inserts are first-class typed operations \+ One tool covers schema, queries, and migrations (drizzle-kit), instead of separate query and migration libraries \+ No ORM-managed connection pool; runs on the standard pg driver, so it composes cleanly with RDS Proxy in Lambda and a normal pool in ECS with no extra tuning \+ Postgres-specific DDL, including partial unique indexes, is expressible directly in the schema definition | \- Smaller ecosystem and fewer third-party integrations than Prisma \- Newer library, shorter track record at scale |
| 3 | Knex.js (query builder) \+ a separate migration tool |  |
|  | \+ Thinnest possible abstraction; every query stays close to hand-written SQL with no surprises \+ Long track record, stable, minimal abstraction | \- No compile-time link between the query builder and the schema; a column rename or type change isn't caught until a query runs at runtime \- Migrations and query building are two separate concerns to keep in sync |
| 4 | TypeORM |  |
|  | \+ Decorator-based, familiar to developers coming from other typed-ORM ecosystems | \- Same raw-SQL-escape-hatch problem as Prisma for the conditional claim and batch insert queries \- Migration generation has a well-documented history of drifting from actual schema state |

## Decision

* Option 2: Drizzle ORM, with drizzle-kit for migrations.  
    
  * The system's hardest queries, the conditional claim, the batch insert, the conflict-aware ingest, the aggregate job list, are not edge cases; they're core to how the pipeline stays correct under concurrency and load. We need a layer that treats these as first-class typed operations, rather than something to drop out of the ORM to write, removing a class of bugs where the type checker can't help because the query is raw SQL.  
  * Running on the standard pg driver rather than owning its own pool means it sits cleanly behind RDS Proxy with no additional configuration, and RDS Proxy is already a fixed requirement of the Lambda worker model (ADR-011).  
  * One tool for schema, queries, and migrations means one thing to keep in sync. Each service's migrations folder (ADR-009) is drizzle-kit output generated from that service's own schema file.


* Ratified by:  
    
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-015: Monorepo build orchestration

# ADR-015: Monorepo build orchestration

## Context

The codebase is a single repo containing 7 packages: React SPA, 3 Node.js services (auth, DocBridge API, document platform), 2 Lambda functions (fan-out, delivery), and one shared package consumed by all of them. npm workspaces is already in use for dependency hoisting and local linking.

## Decision required

**What orchestrates builds, tests, and task ordering across the monorepo packages?**

## Alternatives evaluated

| 1 | npm workspaces only, with explicit root scripts |  |
| ----- | :---- | :---- |
|  | \+ Zero additional tooling: the package manager already installed is the system \+ Task ordering is visible as literal script text rather than inferred from a task graph, so a reader can see what runs and in what order \+ Retrofitting a task runner later is a config file plus a root script rewrite, so deferring costs almost nothing | \- No caching: every CI run rebuilds and retests everything, including untouched packages \- No "affected packages" filtering \- Dependency ordering must be written by hand; npm run build \--workspaces runs in manifest order, not dependency order \- Docker build context per service must be assembled manually |
| 2 | Turborepo on top of npm workspaces |  |
|  | \+ Content-hash caching skips unchanged tasks locally and in CI \+ Declared task graph enforces build order instead of relying on hand-written script sequences \+ turbo prune \--docker generates a pruned lockfile and package subtree, which is the standard answer to per-service image build contexts | \- A second config file and a second execution model to understand alongside npm scripts \- Caching benefit is measured in seconds at seven packages and a two-minute pipeline \- Remote caching needs an account or self-hosted cache server to matter in CI |
| 3 | Nx |  |
|  | \+ Strongest dependency graph analysis and affected-target computation \+ Generators and executors standardize how new packages are created | \- Plugin and executor model abstracts over the underlying tools, so the real build command is one layer removed \- Designed for repos an order of magnitude larger than this one |
| 4 | Separate repository per service |  |
|  | \+ Each service builds and deploys in complete isolation \+ Docker build context problem disappears | \- The shared package becomes a published artifact with versioning and release coordination \- A change spanning API and worker becomes several PRs across several repos |

## Decision

> * Option 1: npm workspaces with explicit root scripts.  
  * At 7 packages and a 2-minute pipeline, a task runner buys seconds of build time in exchange for a permanent layer of indirection. The build sequence is written out literally in the root package.json so that ordering is readable rather than inferred.

> * Ratified by:  
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

# ADR-016: Fan-out mechanism between job submission…

# ADR-016: Fan-out mechanism between job submission and task delivery

## Context

ADR-008 established 2 queues: a job queue carrying one fan-out message per submission, and a task queue carrying one message per task. A fan-out consumer reads the job message, loads the task IDs already written in the submission transaction, and batch-sends 1 message per task.

## Decision required

**Do task messages reach the task queue through a publish/subscribe layer (SNS or EventBridge), or does the fan-out worker send them directly to SQS?**

## Alternatives evaluated

| 1 | Fan-out worker batch-sends directly to the task queue (SQS only) |  |
| ----- | :---- | :---- |
|  | \+ Fewest moving parts on the critical delivery path: 1 producer, 1 queue, 1 consumer \+ SendMessageBatch returns per-message success and failure, so a partial batch failure is visible to the fan-out worker and the job message is retried by the job queue \+ Nothing sits between the producer and the queue that can silently drop or reorder a message | \- Adding a second consumer of delivery events later requires inserting a topic and repointing the producer \- No message filtering layer if task classes diverge |
| 2 | SNS topic between fan-out and the task queue (SNS to SQS subscription) |  |
|  | \+ Adding a second consumer later is a new subscription, with no producer change \+ Message filter policies could route task classes to different queues without producer logic \+ Standard, well-understood AWS pattern | \- We have exactly one consumer of task messages, so the indirection only buys optionality for future \- Adds a hop that can fail independently between producer and queue \- Publish is per-message, not batched to the subscribed queue, so the fan-out worker loses the batch success/failure signal it currently gets from SQS |
| 3 | EventBridge bus with a rule routing job events to the task queue |  |
|  | \+ Content-based routing and schema registry if task events later feed other systems \+ Native integration targets, so a consumer could be added with no code | \- Same objection as Option 2, with more configuration surface \- Higher per-event cost and added latency for a step that is purely internal plumbing |
| 4 | SNS as the fan-out primitive itself, replacing the fan-out worker |  |
|  | \+ Would remove a Lambda function if it worked | \- Included because "use SNS for fan-out" is the reflexive answer, but it doesn’t work: SNS replicates 1 message to many subscribers, it does not expand one message into 2,000 distinct payloads |

## Decision

> * Option 1: the fan-out worker sends task messages directly to the task queue.  
  * The pub/sub pattern exists to decouple one producer from many independent consumers of the same event. This pipeline has one event type with one consumer, and the expansion from job to tasks is a data operation the worker performs against the tasks table, not a routing operation. Inserting SNS would add a hop without removing any coupling.


> * Ratified by:  
  * [Hayk Simonyan](mailto:hayk.simonyan.email@gmail.com)

