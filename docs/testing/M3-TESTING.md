# M3 Testing: Flow 3, Track a job

**Milestone claim (TDD M3):** submit a job, watch the dashboard's task counts and rows flip live, see failure reasons
inline, and survive a page refresh mid-run.

Built: `GET /jobs` (one aggregate query per page), `GET /jobs/:id`, `GET /jobs/:id/tasks` (paginated, status filter),
the shared-team visibility rule (60 s cache), the API Gateway WebSocket API with `$connect` / `subscribe` /
`$disconnect` Lambdas, the `ws_connections` registry with sweeper purge after 2 h (AM-4), no query strings in WS
access logs (AM-7), PostToConnection after every status write (410 deletes the row), and the dashboard: job list,
virtualized task table, live updates, reconnect with refetch.

## 1. Local verification

Locally the `workers-local` container emulates the WebSocket API. It runs the same `WsHandlers` code, and workers push
through the real AWS SDK management client to its `/@connections/{id}` endpoint, so the code path matches AWS.

```bash
dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M3TrackTests"
```

| Requirement | Test |
| :-- | :-- |
| Own jobs, newest first, with aggregate status and counts | `Job_list_shows_own_jobs_with_aggregate_counts` |
| Pagination (2,000 tasks never in one response) + `status=failed` filter with reasons | `Tasks_are_paginated_and_filterable_by_status` |
| Teammate can view; outsider and unknown ids get 404 | `Teammates_can_view_a_job_and_outsiders_get_404` |
| `$connect` rejects missing, invalid, and service tokens | `Websocket_rejects_missing_and_invalid_tokens` |
| Subscribe ack (the client's cue to refetch) | `Subscribed_client_receives_live_task_updates_until_the_job_resolves` |
| Pushes for a 200-task job carry status, counts and aggregate, ending at `completed` | `Pushes_carry_status_and_counts_for_every_task` |
| Outsider can't subscribe | `Outsider_cannot_subscribe_to_a_job` |

Unit: aggregate status mapping (pending / in_progress / completed / completed_with_errors), shared-team intersection
and 60 s cache.

### Browser check

1. Send about 40 files to 5 folders (200 deliveries). **Audit log** lists the job newest-first with a progress bar,
   "x of 200" and a status pill (Waiting → Sending → Delivered; "Partly failed" / "Failed" when tasks fail). Open it:
   the job page shows `● live`, and the waiting/sending/delivered/failed counts and rows move without refreshing.
2. **Refresh mid-run**: you must sign in again (the token is in memory only, by design for v1). Open **Audit log** →
   the job → it catches up (refetch on subscribe), then keeps updating live.
3. Failure: stop the platform for over a minute while a job runs (`docker compose stop platform`, then `start`), or
   use the integration test's never-uploaded path. The headline reads "Finished with failures", the **Failed** tab
   lists the rows, and each shows its reason code (e.g. `RETRIES_EXHAUSTED`, `FILE_NOT_UPLOADED`); **Show more**
   reveals file, destination, cause and attempt count. A never-uploaded file shows "Not uploaded" with Download
   disabled in the Files card.
4. Stop the WS emulator (`docker compose restart workers-local`): the badge goes `○ reconnecting`, then back to
   `● live`, and counts re-sync.

## 2. Deploy to AWS (dev)

`./scripts/deploy.sh dev` creates the WebSocket API (`live` stage, routes `$connect`/`$disconnect`/`subscribe`), the
three `ws-*` Lambdas, the `execute-api:ManageConnections` grants for the pushing functions, and the account-level API
Gateway CloudWatch role (in `infra/envs/global`).

## 3. Verify on AWS (dev)

```bash
cd infra/envs/dev
WS=$(terraform output -raw ws_url)
aws apigatewayv2 get-apis --query "Items[?Name=='docbridge-dev-ws'].[ApiId,ProtocolType,RouteSelectionExpression]"
# AM-7: the access log format has no query string fields.
aws apigatewayv2 get-stage --api-id <ws api id> --stage-name live --query 'AccessLogSettings.Format'
```

Then the M3 suite with `DOCBRIDGE_WS_URL=$WS` (and the M1-TESTING §3 variables). Browser: repeat the checks at the
CloudFront URL. The live badge connects to `wss://…execute-api…/live`.

## 4. Production

As in M1-TESTING §4.
