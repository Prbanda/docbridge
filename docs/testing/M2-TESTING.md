# M2 Testing: Flow 2, Distribute a batch

**Milestone claim (TDD M2):** a member selects up to 100 files, watches parallel uploads with progress, maps files to
destinations, submits, and gets a confirmation with the task count in under 500 ms. Deliveries land in the platform,
verified through the platform API, including across a platform kill-and-recover.

Built: `POST /files` (presigned POST with size, type and SHA-256 conditions; multipart plan above 100 MB), URL re-issue
(AM-2), multipart completion (G9), `POST /jobs` (one transaction, atomic file claim AM-1, `Idempotency-Key` AM-5,
membership plus docbridge-enabled checks), job + task queues with DLQs, the fan-out and delivery Lambdas, DLQ
consumers, the sweeper (republish G8, orphan files AM-3), and web chunk 2.

---

## 1. Local verification

```bash
docker compose up --build -d && ./scripts/smoke-local.sh
dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M2DistributeTests"
./scripts/drill-local.sh        # platform outage, retries exhausted → DLQ, sweeper republish (§1.2)
```

### 1.1 Automated checks

| Requirement | Test |
| :-- | :-- |
| Type allow-list (no executables), 1 GB ceiling, SHA-256 format, ≤ 100 files per request | `Files_endpoint_validates_type_size_checksum_and_count` |
| Presigned POST carries the checksum; storage rejects oversize and corrupted bytes; 15-min expiry (ADR-004) | `Presigned_post_carries_checksum_and_size_conditions_and_storage_enforces_them` |
| Expired URLs are re-issued for the same file, owner only (AM-2) | `Upload_url_can_be_reissued_for_an_unsubmitted_file_only_by_its_owner` |
| 5 files × 4 destinations: 201 with task count, full task list pending at once, all delivered, checksums match in the platform | `Batch_is_acknowledged_fast_and_every_task_is_delivered_intact` |
| Same `Idempotency-Key` returns the same job | `Idempotency_key_replay_returns_the_same_job` |
| 4 concurrent submits of the same files → exactly one 201 (AM-1) | `Concurrent_submissions_of_the_same_files_claim_them_once` |
| Foreign team 403, disabled team 403, someone else's files 409 | `Submission_rejects_foreign_teams_disabled_teams_and_other_users_files` |
| Never-uploaded file → `FILE_NOT_UPLOADED` with file + destination in the reason; sibling unaffected | `Never_uploaded_file_fails_its_task_with_file_not_uploaded` |
| Membership revoked after submit → `NOT_AUTHORIZED_AT_DELIVERY` on that destination only (ADR-013) | `Membership_revoked_after_submission_fails_only_that_destination` |
| Platform ingest: same task twice → one document (200 replay); checksum mismatch → 422, never stored; user token can't ingest | `Platform_ingest_is_idempotent_per_task_and_rejects_checksum_mismatch` |
| > 20 destinations / empty job → 400 | `Limits_are_enforced_at_submission` |
| 110 MB multipart upload delivered with intact SHA-256 | `Multipart_upload_above_100_mb_is_delivered_intact` |

Unit tests: mapping expansion and limits (2,000 tasks max), presigned policy conditions, delivery outcome mapping
(201/200 → completed, 403/404/422/400 → permanent with code, 5xx/429/401/network → retry), digest survives stream
disposal, service-token caching and background refresh.

### 1.2 Failure drills (`./scripts/drill-local.sh`)

| Drill | Expected |
| :-- | :-- |
| Platform stopped for ~8 s right after submitting 40 tasks | all 40 complete, some with `attemptCount > 1`, 0 failed, task DLQ depth unchanged |
| Platform stopped for longer than every retry (local backoff 10 s, 20 s) | all tasks `failed` with `RETRIES_EXHAUSTED: … attempt 3: …` |
| API publishes to a missing queue (job commits, message lost) | 201 anyway; the sweeper republishes within ~90 s; job completes |

### 1.3 Browser check

1. <http://localhost:8080> as alice → **Browse**.
2. Tick 2–3 destinations: hover a folder icon in the tree (or in search results) and it turns into a checkbox.
   Binders and teams never show a checkbox; only folders can receive files. Each tick adds a chip above the queue, and the drop zone reads "Drop files to send to N folders".
   Before anything is ticked it reads "Tick folders in the tree, then drop files here" and refuses files.
3. Drop (or **Browse** for) 5–10 PDF/PNG files: each row shows `checking…` (SHA-256 in a Web Worker) then `ready`.
   An `.exe` is rejected with a message. The summary reads `N files → 3 folders · 3N deliveries`.
4. Per-file mapping: click "All 3 folders" on one file and untick a folder; it now reads "2 of 3 folders" and the
   delivery count drops by one.
5. **Send**: the dialog shows per-file upload progress (files go straight to storage), then "Uploaded". Try
   **Minimize** mid-upload: a pill stays bottom-right while you browse, and **Audit log** shows an "Uploading" row.
   **Open in audit log** opens the new job.
6. Back in **Browse**, open a target folder: the delivered documents are in its table (name, type, size, date).

---

## 2. Deploy to AWS (dev)

`./scripts/deploy.sh dev`. M2 adds: SQS job and task queues + DLQs (`maxReceiveCount 3`, visibility 120 s / 1800 s),
the `fanout`, `delivery`, `task-dlq`, `job-dlq` and `sweeper` Lambdas (dotnet10, in the VPC, DB through RDS Proxy),
the 15-minute EventBridge schedule, the platform document bucket, and the DLQ-depth and delivery-error alarms.

Dev sizing note: this account's Lambda concurrency quota is **10** and AWS keeps all 10 unreserved, so dev caps
delivery with the SQS trigger's maximum concurrency (5) instead of TDD 2.4's reserved concurrency of 50. Prod sets 50
and needs a quota increase first (§4).

## 3. Verify on AWS (dev)

```bash
cd infra/envs/dev
aws sqs get-queue-attributes --queue-url "$(terraform output -raw task_queue_url)" --attribute-names RedrivePolicy VisibilityTimeout
aws lambda get-function-configuration --function-name docbridge-dev-delivery --query '{runtime:Runtime,timeout:Timeout,mem:MemorySize,vpc:VpcConfig.SubnetIds}'
aws lambda list-event-source-mappings --function-name docbridge-dev-delivery --query 'EventSourceMappings[0].[State,FunctionResponseTypes[0],ScalingConfig.MaximumConcurrency]'
aws s3api get-bucket-encryption --bucket "$(aws s3api list-buckets --query "Buckets[?contains(Name,'docbridge-dev-platform-docs')].Name" --output text)"
```

Then the M2 suite and the AWS drills (environment variables as in M1-TESTING §3, plus):

```bash
export DOCBRIDGE_TASK_QUEUE_URL=$(cd infra/envs/dev && terraform output -raw task_queue_url)
dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M2DistributeTests"
DOCBRIDGE_DRILLS=aws DOCBRIDGE_TASK_DLQ_URL=<task DLQ url> \
  dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M5DrillTests"
```

On AWS the outage drill scales the platform ECS service to 0 and back to 2. With the production backoff (60 s, 300 s)
the retry window is about 6 minutes, so the 45 s outage drains with zero failures. The retries-exhausted drill takes
about 11 minutes. On AWS it pauses the delivery Lambda's SQS trigger while the job is accepted (submission needs the
platform up for the membership check) and resumes it once the platform is gone; otherwise every delivery finishes
before ECS has drained the platform. The sweeper drill is local-only, because it needs a deliberately broken queue
URL on the API.

Or all of it at once: `./scripts/test-aws.sh dev --load --drills`.

Browser: repeat §1.3 at the CloudFront URL.

## 4. Production

As in M1-TESTING §4. **Before the first prod apply**, request a Lambda concurrent-executions quota above 60
(Service Quotas → AWS Lambda → Concurrent executions), or the delivery function's reserved concurrency of 50 fails to apply.
