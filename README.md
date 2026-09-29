# DocBridge

Batch file distribution for clinical trial documents: pick files once, map them
to many destinations, submit as one batch, track each delivery to success or
failure.

Read in this order if you are new to the repo.

## 1. How the system is split

| Piece | Role |
| :-- | :-- |
| `services/auth` | Register, login, issue JWTs, expose JWKS, issue service tokens |
| `services/platform` | Teams → binders → folders; document ingest; membership checks |
| `services/docbridge-api` | Destinations proxy, file upload URLs, jobs/tasks, downloads |
| `workers/fanout` | Reads a job message, publishes one message per task |
| `workers/delivery` | Claims a task, checks the staged file, delivers to platform |
| `workers/ws` | WebSocket connect / subscribe / disconnect |
| `packages/shared` | Shared JWT checks and API error shape (`@docbridge/shared`) |
| `web` | React SPA (starts in M1) |

## 2. Set it up

[docs/SETUP.md](docs/SETUP.md) — step-by-step: AWS + CI/CD once, then local and
dev up/down. IAM user/CLI: [docs/AWS_SETUP.md](docs/AWS_SETUP.md). Pipeline
reference: [docs/CI_CD.md](docs/CI_CD.md).

## 3. Prove M0 works

| Doc | Use when |
| :-- | :-- |
| [docs/testing/M0-TESTING.md](docs/testing/M0-TESTING.md) | Step-by-step local + AWS checks |
| [docs/testing/M0-VALIDATION.md](docs/testing/M0-VALIDATION.md) | Short pass/fail acceptance list |

## 4. Design background

| Doc | What it is |
| :-- | :-- |
| [docs/design/Design Requirements.md](docs/design/Design%20Requirements.md) | Problem and product constraints |
| [docs/design/DocBridge Blueprint.md](docs/design/DocBridge%20Blueprint.md) | High-level design |
| [docs/design/DocBridge ADRs.md](docs/design/DocBridge%20ADRs.md) | Decisions (why X over Y) |
| [docs/design/DocBridge Technical Design.md](docs/design/DocBridge%20Technical%20Design.md) | Spec the code follows |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | Milestone order and definition of done |


Supporting folders:

| Path | Role |
| :-- | :-- |
| `infra/` | Terraform modules + `envs/{global,dev,prod}` |
| `bootstrap/` | One-time script to create the Terraform state bucket |
| `docker/` | Local Postgres + LocalStack init scripts |
| `scripts/` | Local smoke test; AWS up/down helpers |
| `docs/` | Setup, design, and milestone testing |

Requires Node ≥22. Docker for local. AWS CLI + Terraform ≥1.15.8 for AWS.
