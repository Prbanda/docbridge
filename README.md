# DocBridge

Batch file distribution for clinical trial documents: pick files once, map them
to many destinations, submit as one batch, track each delivery to success or
failure.

Read in this order if you are new to the repo.

## 1. How the system is split

Backend: C# / .NET 10 (ADR-017…019). Web: React + TypeScript.

| Piece | Role |
| :-- | :-- |
| `services/auth` | Register, login, issue JWTs, expose JWKS, issue service tokens |
| `services/platform` | Teams → binders → folders; document ingest; membership checks; team admin |
| `services/docbridge-api` | Destinations proxy, file upload URLs, jobs/tasks, downloads |
| `workers/fanout` | Reads a job message, publishes one message per task (Lambda) |
| `workers/delivery` | Claims a task, checks the staged file, delivers to the platform (Lambda) |
| `workers/ws` | WebSocket connect / subscribe / disconnect (Lambda) |
| `workers/maintenance` | DLQ consumers + the 15-minute sweeper (Lambda) |
| `workers/common` | The worker logic all of the above share |
| `workers/local-runner` | Compose stand-in for Lambda triggers + the WebSocket API |
| `packages/shared` | JWT validation, error shape, DB/migrations, service-token and platform clients |
| `tools/db-bootstrap` | Creates the 3 logical databases + roles in RDS (Lambda, run by Terraform) |
| `tools/seed` | Demo users, teams, binders, nested folders |
| `web` | React SPA |

## 2. Set it up

[docs/SETUP.md](docs/SETUP.md): AWS + CI/CD once, then local and dev up/down.
IAM user/CLI: [docs/AWS_SETUP.md](docs/AWS_SETUP.md). Pipelines: [docs/CI_CD.md](docs/CI_CD.md).

Local, in four commands:

```bash
docker compose up --build -d
./scripts/smoke-local.sh
./scripts/seed.sh local
open http://localhost:8080        # alice@demo.docbridge.test / Demo-Password-2026
```

The seed creates two teams, **Helios Therapeutics** (US; binders Study HX-101 and Study HX-204) and **Nordlicht
Clinical Sites** (DE; binders Site Berlin and Site Hamburg), with nested folders and no files. alice is in both, bob
only in Helios, carol in none. To start over: `docker compose down -v`, bring it up again and re-seed. To seed next to
existing data under new emails: `SEED_EMAIL_DOMAIN=studies.docbridge.test ./scripts/seed.sh local`.

AWS dev: `./scripts/deploy.sh dev && ./scripts/seed.sh dev`. Tear down between sessions with `./scripts/aws-down.sh`.

## 3. Prove each milestone works

| Doc | Milestone |
| :-- | :-- |
| [docs/testing/M0-TESTING.md](docs/testing/M0-TESTING.md) | Foundations |
| [docs/testing/M1-TESTING.md](docs/testing/M1-TESTING.md) | Flow 1: browse destinations |
| [docs/testing/M2-TESTING.md](docs/testing/M2-TESTING.md) | Flow 2: distribute a batch |
| [docs/testing/M3-TESTING.md](docs/testing/M3-TESTING.md) | Flow 3: track a job |
| [docs/testing/M4-TESTING.md](docs/testing/M4-TESTING.md) | Flow 4: retrieve a file |
| [docs/testing/M5-TESTING.md](docs/testing/M5-TESTING.md) | Hardening gates + the consolidated results |

Test suites: `dotnet test --project tests/DocBridge.UnitTests`, and `tests/DocBridge.IntegrationTests` against
local compose or a deployed URL (`DOCBRIDGE_BASE_URL`).

## 4. Design background

| Doc | What it is |
| :-- | :-- |
| [docs/design/Design Requirements.md](docs/design/Design%20Requirements.md) | Problem and product constraints |
| [docs/design/DocBridge Blueprint.md](docs/design/DocBridge%20Blueprint.md) | High-level design |
| [docs/design/DocBridge ADRs.md](docs/design/DocBridge%20ADRs.md) | Decisions (why X over Y) |
| [docs/design/DocBridge ADRs - DotNet.md](docs/design/DocBridge%20ADRs%20-%20DotNet.md) | ADR-017…019: the .NET backend |
| [docs/design/DocBridge Technical Design.md](docs/design/DocBridge%20Technical%20Design.md) | Spec the code follows |
| [docs/PLAN-M1-M5.md](docs/PLAN-M1-M5.md) | M1–M5 task plan, TDD gaps and how each is closed |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | Milestone order, amendments AM-1…AM-7 |

Supporting folders:

| Path | Role |
| :-- | :-- |
| `infra/` | Terraform modules + `envs/{global,dev,prod}` (`modules/app` composes the application tier) |
| `bootstrap/` | One-time script to create the Terraform state bucket |
| `docker/` | Local Postgres + LocalStack init, the local edge (nginx) |
| `scripts/` | deploy, seed, smoke, drills, Lambda packaging, AWS up/down |
| `tests/` | Unit and integration suites |

Requires the .NET 10 SDK, Docker, Node ≥ 20 (web). AWS CLI + Terraform ≥ 1.15.8 for AWS.
