# Requirements trace: M1–M5

Every requirement in `docs/design/Design Requirements.md`, every milestone claim in the TDD, and every instruction in the
build request, each with **YES** (met, with evidence) or **NO** (not met, with the reason). Evidence names a test, a
command result, or a file. "Local" = docker compose on the dev machine; "AWS" = the deployed dev environment
(`https://y6l3t0rfze.execute-api.us-east-1.amazonaws.com`, SPA `https://djr1rktaa13wa.cloudfront.net`).
Run date: 2026-10-22.

## A. Functional requirements

| # | Requirement | Met | Evidence |
| :-- | :-- | :-: | :-- |
| 1 | Destination discovery reflects the user's real permissions | YES | `M1BrowseTests.User_sees_only_member_teams_that_have_docbridge_enabled`, `Contents_of_another_teams_binder_are_forbidden…`, local + AWS |
| 2 | Select multiple files, map each to one or more destinations | YES | `JobEndpoints.Expand` + unit tests (100 × 20 = 2,000), `M2…Batch_is_acknowledged_fast…` (5 × 4), web mapping matrix |
| 3 | Submitting creates a job, acknowledged immediately; users see active and past jobs | YES | 201 + task count; p99 329 ms local, 257 ms on AWS (10 × 2,000 tasks); `M3…Job_list_shows_own_jobs…` |
| 4 | Each file × destination is a task, processed asynchronously and independently, decoupled from submission | YES | job queue → fan-out → task queue → delivery (ADR-008/016); isolation gate 1 failed / 1,900 completed (local + AWS) |
| 5 | Task lifecycle pending → in_progress → completed / failed; aggregate job status; automatic updates | YES | `TaskCounts.Aggregate` unit tests; WebSocket pushes `M3…Pushes_carry_status_and_counts…`; dashboard live badge |
| 6 | File arrives intact; delivery failures handled and surfaced with detail | YES | SHA-256 verified by S3 (single-part), worker HEAD + streaming hash, platform recompute; `…ingest_is_idempotent…rejects_checksum_mismatch`; failure reasons name file, destination, cause |
| 7 | System manages users and credentials; login and token issuance; team-based authorization | YES | auth service (bcrypt-12, RS256, JWKS, client credentials); `M1…Service_tokens_are_scoped`, `Register_login_issues…` |
| 8 | Download any file of a teammate's job; downloads don't flow through app servers | YES | `M4…Teammate_downloads_the_exact_bytes_and_outsider_gets_404`; AWS URL host `…staging….s3.us-east-1.amazonaws.com` |

## B. Non-functional requirements

| Requirement | Met | Evidence |
| :-- | :-: | :-- |
| 2,000 registered / 200 concurrent users | YES (by design) | stateless services × 2 tasks behind the ALB, gateway throttling 200 rps / 500 burst; not load-tested at 200 users |
| Up to 100 files, 20 destinations, 2,000 tasks per job | YES | limits enforced (`Limits_are_enforced_at_submission`, unit tests); 10 × 2,000-task jobs delivered, local + AWS |
| 90,000 tasks/day | YES (by design) | ~1 task/s average; the dev drain rate (5 concurrent) already exceeds it; prod raises delivery concurrency to 50 |
| 1 GB max file, PDF/DOCX/XLSX/PNG/JPG only | YES | POST policy `content-length-range`, allow-list with extension match (server + client), 110 MB multipart test |
| 99.9 % availability | YES (by design) | 2 tasks per service across 2 AZs, managed SQS/Lambda/S3; RDS Multi-AZ in prod; an RDS reboot was absorbed with 0 failed requests 30 s after recovery (keepalive). No long-running availability measurement |
| Submission ack < 500 ms p99 regardless of batch size | YES | final code: local p99 329 ms (median 204 ms); AWS p99 257 ms client-side (median 197 ms), 105–184 ms at the gateway, on 10 × 2,000-task jobs. Holds on prod's DB class (db.t4g.small); on db.t4g.micro it degraded to 0.8–2 s once tables outgrew its cache (M5-TESTING §5.3) |
| Destination browser < 1 s with 50 teams | YES | local max 9 ms, AWS max 118 ms (`Destination_browser_loads_50_teams…`) |
| UI never freezes during selection and upload | YES (by design) | SHA-256 in a Web Worker in 8 MB slices; XHR uploads, 4 concurrent. Manual throttled-network check (M5 §4) not run by the agent |
| No silent loss; ≤ 0.1 % failures; every failure recorded | YES | 0 unexpected failures across all runs; DLQ consumer → `RETRIES_EXHAUSTED` drill; FILE_NOT_UPLOADED / NOT_AUTHORIZED / CHECKSUM reasons |
| ≥ 3 automatic attempts for transient failures | YES | `maxReceiveCount 3` + backoff; drill asserts `attemptCount == 3` and `attempt 3` in the reason |
| Partial failure acceptable; a failed task doesn't affect others | YES | isolation gate; `Never_uploaded_file…` sibling completes |
| Encryption at rest via cloud KMS, no own crypto | YES | SSE-KMS + Bucket Keys on staging and platform buckets (AWS check), RDS storage encrypted (M0) |
| End-to-end integrity verifiable | YES | client SHA-256 → S3 checksum → worker → platform 422 on mismatch; download test compares SHA-256 |
| Files only accessible to the submitting user's team members | YES | presign only after authorization; 404 for outsiders (M3/M4 tests); BPA on buckets; TLS-only policies |
| No secrets in source; injected at runtime | YES | Secrets Manager + ECS `secrets` / Lambda `*_SECRET_ARN`; gitleaks run: 2 findings, both false positives, marked `gitleaks:allow` |
| All endpoints require auth; unauthenticated rejected | YES | gateway 401 (AWS `curl`), services re-validate (`Unauthenticated_*` tests); fallback authorization policy |
| Staged files retained 30 days, then lifecycle-deleted | YES | S3 lifecycle (M0); sweeper purges never-submitted file rows (AM-3) |
| Automated deployment, no manual steps beyond credentials | YES | `scripts/deploy.sh dev --yes` ran end to end (Terraform incl. DB bootstrap, images, Lambda code, ECS, SPA) |
| All config from env vars / secrets manager | YES | `Env.*` everywhere; compose and Terraform supply it |
| Services runnable locally via docker compose | YES | compose runs auth, platform, docbridge-api, workers, edge; smoke 20/20 |

## C. TDD milestone claims

| Milestone | Claim | Met | Evidence |
| :-- | :-- | :-: | :-- |
| M1 | Log in, browse the team/binder/folder tree, filtered to memberships | YES | M1 suite local + AWS; seeded demo users |
| M2 | Select files, watch uploads, map, submit, confirmation; documents land in the platform, incl. kill-and-recover | YES | M2 suite; outage drill local + AWS (400 tasks, 0 failed, retries observed) |
| M3 | Task counts and rows flip live; failure reasons inline; survive a refresh | YES | M3 suite (pushes, ack, catch-up refetch); refresh catch-up exercised by the subscribe-ack test, not by a browser |
| M4 | Download returns the file; 404 outside the submitter's teams | YES | M4 suite local + AWS; expiry 403 on real S3 |
| M5 | Success metrics proven on the deployed system, gating prod | YES (dev) | §5 of M5-TESTING; prod apply deliberately left to the approval gate |

## D. Build-request instructions

| Instruction | Met | Evidence |
| :-- | :-: | :-- |
| Read Design Requirements, Blueprint, ADRs, TDD; confirm clarity; plan | YES | `docs/PLAN-M1-M5.md` (gaps G1–G13 and their resolutions) |
| List what's needed from you (cloud, env vars) | YES | PLAN §4 and the hand-off message |
| Backend APIs in C#/.NET instead of Node | YES | all services and workers are .NET 10; ADR-017/018/019 record the change |
| One testing-strategy doc per milestone: local, AWS deploy, production test | YES | `docs/testing/M1…M5-TESTING.md` |
| Run a testing round so everything in the docs works before handing back | YES, with the exceptions in §E | results in M5-TESTING §5 |
| Each task names inputs and outputs | YES | PLAN §3 tables |
| Walk the task line by line, YES/NO with evidence | YES | this document |

## E. Not verified by the agent (honest gaps)

| Item | Status | Why / what to do |
| :-- | :-: | :-- |
| Production deploy and prod verification | NO | Prod is gated on your approval in the `prod` GitHub environment (TDD launch plan). It also needs a Lambda concurrency quota increase above 60 before the delivery function's reserved concurrency of 50 can apply (the account limit is 10). |
| GitHub Actions workflows (rewritten for .NET) | NO | Not pushed: pushing to GitHub is your call. `dotnet build`, `dotnet format --verify-no-changes`, unit tests, Lambda packaging, web build and `terraform validate` all pass locally, which is what the workflows run. |
| Manual browser checks (throttled-upload UX, multipart resume after going offline, WS reconnect by network toggle) | NO | No browser automation in this session. The underlying code paths are covered by automated tests (multipart delivery, reconnect logic exercised via subscribe/ack); run M5-TESTING §4 by hand. |
| 200-concurrent-user load | NO | Not part of the TDD's M5 list; the system is sized for it by design. |
