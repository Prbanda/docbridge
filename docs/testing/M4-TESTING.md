# M4 Testing: Flow 4, Retrieve a file

**Milestone claim (TDD M4):** the download button returns the file, and anyone outside the submitter's teams gets 404.

Built: `POST /files/:fileId/download-url`. The requester must share a team with the job's submitter. The response is a
2-minute presigned GET for that single object key (ADR-002/004), the answer outside that scope is 404 (not 403), and
the bytes never touch application servers.

## 1. Local verification

```bash
dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M4RetrieveTests"
```

| Requirement | Test |
| :-- | :-- |
| Submitter and teammate get a URL (`expiresIn: 120`), served from storage rather than the API host, with the exact bytes (SHA-256 compared) | `Teammate_downloads_the_exact_bytes_and_outsider_gets_404` |
| Outsider and unknown file → 404 | same test |
| Removing someone from the team revokes their access (after the 60 s cache) | `Former_teammate_loses_download_access` |

Unit: the download URL carries `X-Amz-Expires=120` and the single object key.

### Browser check

1. As alice, **Audit log** → a finished job → **Download** next to a file (Files card): it downloads straight from LocalStack
   (`localhost:4566`).
2. As bob (shares Helios Therapeutics with alice; use a job sent to a Helios folder) open the same job URL (`#/audit/<id>`): view and download work.
3. As carol (shares none): the job page shows "Job not found".

## 2. Deploy to AWS (dev)

No new infrastructure. The `docbridge-api` task role holds `s3:GetObject` on `staging/*`, which the presigned GET
inherits, and the staging bucket's CORS allows the CloudFront origin.

## 3. Verify on AWS (dev)

Run the M4 suite against the gateway (M1-TESTING §3 variables). Expiry check (real S3 enforces it; LocalStack doesn't):

```bash
URL=$(curl -s -X POST -H "Authorization: Bearer $TOKEN" "$API/files/<fileId>/download-url" | node -pe 'JSON.parse(require("fs").readFileSync(0)).url')
curl -s -o /dev/null -w '%{http_code}\n' "$URL"     # 200
sleep 125; curl -s -o /dev/null -w '%{http_code}\n' "$URL"   # 403 Request has expired
```

## 4. Production

As in M1-TESTING §4.
