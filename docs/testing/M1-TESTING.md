# M1 Testing: Flow 1, Browse destinations

**Milestone claim (TDD M1):** a member registers, logs in, and browses the team → binder → folder tree in the browser,
filtered to their memberships, in under 1 s per level. Built in this milestone: the auth service (register, login,
RS256 + JWKS, service tokens), the platform (hierarchy, membership route, ingest, admin routes), the public edge
(API Gateway + JWT authorizer + VPC Link + internal ALB), the DocBridge API browse proxy, web chunk 1, seed data,
and the DB bootstrap.

**Stack note:** the backend is C#/.NET 10 (ADR-017…019 in `docs/design/DocBridge ADRs - DotNet.md`).

## Prerequisites (once)

| Tool | Version | Check |
| :-- | :-- | :-- |
| .NET SDK | 10.0.100 or later (`global.json`) | `dotnet --list-sdks` |
| Docker Desktop | any recent | `docker info` |
| Node | 22 (web build; 20 also works) | `node --version` |
| AWS CLI v2, Terraform ≥ 1.15.8 | for §2–§4 only | `aws sts get-caller-identity` |

---

## 1. Local verification

```bash
docker compose up --build -d          # postgres, localstack, auth, platform, docbridge-api, workers-local, edge
docker compose ps                     # every service "healthy" (edge has no health check)
./scripts/smoke-local.sh              # expect: Result: 20 passed, 0 failed
./scripts/seed.sh local --perf        # demo users, 6 teams (1 DocBridge-disabled), nested folders, + perf user (50 teams)
dotnet test --project tests/DocBridge.UnitTests
dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M1BrowseTests"
```

### 1.1 What the automated M1 tests prove

| Requirement | Test (`tests/DocBridge.IntegrationTests/M1BrowseTests.cs`) |
| :-- | :-- |
| All endpoints require auth; unauthenticated requests rejected | `Unauthenticated_requests_are_rejected`, `Unauthenticated_writes_are_rejected`, `Forged_token_is_rejected` |
| Login issues a 15-min RS256 JWT with `kid`, `sub`, `email`, `aud` | `Register_login_issues_a_15_minute_rs256_jwt` |
| Generic duplicate-email error; identical wrong-password / unknown-email responses | `Duplicate_registration_gets_a_generic_error`, `Wrong_password_and_unknown_email_look_identical` |
| JWKS + discovery are public (the gateway authorizer needs them) | `Jwks_and_discovery_document_are_public` |
| Service tokens are scoped; user and service tokens can't cross over (ADR-013) | `Service_tokens_are_scoped` |
| Only member teams with DocBridge enabled are listed; foreign team → 403 | `User_sees_only_member_teams_that_have_docbridge_enabled` |
| One level per expand, nested folders | `Tree_expands_one_level_at_a_time_including_nested_folders` |
| Foreign binder → 403, folder from another binder → 404 | `Contents_of_another_teams_binder_are_forbidden_and_foreign_folders_not_found` |
| Destination browser < 1 s for a user with 50 teams | `Destination_browser_loads_50_teams_well_under_one_second` |

Unit tests (`tests/DocBridge.UnitTests/TokenTests.cs`): wrong issuer, wrong audience, foreign signing key, tampered payload
(a forged `platform:admin` service claim), JWKS exposes no private exponent, no implicit ephemeral key.

### 1.2 Browser check

1. Open <http://localhost:8080>. Sign in as `alice@demo.docbridge.test` / `Demo-Password-2026`.
2. **Browse**: the sidebar lists 2 teams with region badges: "Helios Therapeutics" (US) and "Nordlicht Clinical
   Sites" (DE). The first team opens on the right with its binders (Study HX-101, Study HX-204). Folders start empty.
3. Expand Helios Therapeutics → Study HX-101 → "Safety Reports" → "SUSARs" with the chevrons, or click rows in the
   table on the right. Nordlicht's Site Berlin goes three levels deep: "Monitoring Visits" → "2026" → "Q3". Each level loads when opened; the breadcrumb and "N folders · M files" follow you.
4. The bottom of the sidebar shows the teams load time (`… loaded in N ms`).
5. Type `Amendments` in **Find teams, binders, folders**: both "Amendments" folders (Study HX-101 and Study HX-204)
   appear with their paths, including branches you haven't expanded. Carol searching the same finds nothing.
6. Sign in as `bob@demo.docbridge.test`: only Helios Therapeutics. Sign in as `carol@demo.docbridge.test`: the empty
   state "You are not a member of any DocBridge-enabled team".
7. Sign in as `perf@demo.docbridge.test`: 50 teams, load time well under 1000 ms.
8. Register a new account from the login page; it lands on an empty destination list (no memberships).

---

## 2. Deploy to AWS (dev)

One command (same steps CI runs on `main`):

```bash
./scripts/deploy.sh dev            # asks before each terraform apply; add --yes for non-interactive
./scripts/seed.sh dev --perf
```

Order inside the script follows ADR-001's rollout note: services and the internal ALB come up and pass health checks
inside the VPC before the gateway routes to them. Terraform creates, in this order of dependency:
secrets (JWT key, 3 client secrets) → `db-bootstrap` Lambda, **invoked by Terraform** to create the 3 logical
databases + roles in RDS → internal ALB + ECS services (2 tasks each, across AZs) → HTTP API with JWT authorizer
and VPC Link. Deploy outputs print the SPA, API and WebSocket URLs.

---

## 3. Verify on AWS (dev)

```bash
cd infra/envs/dev
API=$(terraform output -raw api_url); SPA=$(terraform output -raw spa_url)

# 3.1 The gateway rejects unauthenticated traffic before it reaches the VPC (ADR-001).
curl -s -o /dev/null -w '%{http_code}\n' "$API/destinations/teams"           # 401
aws logs tail /apigw/docbridge-dev-http --since 5m | grep '"status":"401"'    # authError present, no integration latency

# 3.2 Public routes work through the gateway.
curl -s "$API/.well-known/openid-configuration"                             # issuer == $API
curl -s "$API/.well-known/jwks.json" | head -c 120

# 3.3 Services are healthy behind the internal ALB, 2 tasks each.
aws ecs describe-services --cluster docbridge-dev --services auth platform docbridge-api \
  --query 'services[].[serviceName,runningCount,desiredCount]' --output table

# 3.4 The ALB is not reachable from the internet (internal scheme, private subnets).
aws elbv2 describe-load-balancers --names docbridge-dev-internal --query 'LoadBalancers[0].Scheme'   # "internal"

# 3.5 Logical databases exist and are isolated: db-bootstrap ran from Terraform.
aws logs tail /aws/lambda/docbridge-dev-db-bootstrap --since 1d | grep "db-bootstrap done"
```

Then the same automated M1 suite against the deployed edge:

```bash
export DOCBRIDGE_BASE_URL="$API"
export DOCBRIDGE_WS_URL=$(terraform output -raw ws_url)
export PLATFORM_ADMIN_CLIENT_SECRET=$(aws secretsmanager get-secret-value --secret-id "$(terraform output -raw platform_admin_secret_arn)" --query SecretString --output text)
export DELIVERY_WORKER_CLIENT_SECRET=$(aws secretsmanager get-secret-value --secret-id "$(terraform output -raw delivery_worker_secret_arn)" --query SecretString --output text)
cd ../../..
dotnet test --project tests/DocBridge.IntegrationTests -- --filter-class "DocBridge.IntegrationTests.M1BrowseTests"
```

Browser: open `$SPA` and repeat §1.2.

---

## 4. Production

Prod uses the same modules (`infra/envs/prod`, larger RDS, Multi-AZ, deletion protection). It is applied **only** through
the `prod` GitHub environment's manual approval, after the M5 gates pass in dev:

1. Merge to `main` → the `infra` workflow applies dev, then waits on the `prod` approval.
2. Approve → prod apply. Then run `./scripts/deploy.sh prod` (or the service workflows) to push images and code.
3. Repeat §3 with `infra/envs/prod`. Seed only a test team; don't seed demo users in prod.

---

## 5. Results of the agent's testing round

See the table at the end of `M5-TESTING.md` (one consolidated run covers M1–M5, local and AWS dev).
