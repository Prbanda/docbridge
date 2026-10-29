# DocBridge ADRs — .NET backend (ADR-017 … ADR-019)

These extend `DocBridge ADRs.md`. They were triggered by one product-owner decision made after M0:
**all backend code (services and workers) is written in C# on .NET instead of Node.js/TypeScript.**
The web client stays React + TypeScript. Every other ADR (edge, presigned transfers, encryption,
expiry, SQS, three services, WebSockets, queue topology, DB topology, PostgreSQL, Lambda workers,
Terraform, service tokens, direct SQS fan-out) is unchanged; only the ADRs that named a Node
library are superseded.

---

## ADR-017: Backend runtime and web framework

### Context
The product owner requires C#/.NET for the backend. M0 shipped Node skeletons (health endpoints
only), so the switch costs a skeleton rewrite and CI changes, nothing more. Lambda workers
(ADR-011) need a managed runtime that outlives the project: the `dotnet8` runtime is deprecated in
November 2026; `dotnet10` (LTS, supported to November 2028) has been GA in Lambda since January 2026.

### Decision required
**Which .NET version and web stack do the services and workers use?**

| 1 | **.NET 10 (LTS) everywhere: ASP.NET Core minimal APIs for services, `dotnet10` managed Lambda runtime for workers** | |
| :-: | :-- | :-- |
| | + One LTS version for services and workers until Nov 2028<br>+ Minimal APIs map 1:1 onto the TDD's route tables<br>+ Managed runtime: AWS patches it, no custom bootstrap | − Larger Lambda package than Node; cold starts ~300–600 ms (no SnapStart for .NET 10 assumed) |
| 2 | .NET 8 (LTS) | |
| | + Longest track record | − Lambda `dotnet8` deprecated Nov 2026, during this project |
| 3 | .NET 10 Native AOT on `provided.al2023` | |
| | + Fastest cold start | − Reflection limits (JSON, AWS SDK trimming warnings), slower builds, harder debugging |

### Decision
Option 1. Cold start only touches the first message of a burst, and SQS absorbs it.
Workers and services share one class library (`packages/shared`) for JWT validation, error shape,
DB connection building, migrations, service tokens and the platform client: the TDD's "one
implementation, both services import it".

---

## ADR-018: Database access layer (supersedes ADR-014)

### Context
ADR-014 chose Drizzle because the system's correctness-critical queries are SQL-shaped:
conditional claim `UPDATE … WHERE status IN (…) RETURNING`, `ON CONFLICT DO NOTHING RETURNING`,
a 2,000-row batch insert in one transaction, an aggregate `count(*) FILTER` page query, and
`UNIQUE NULLS NOT DISTINCT`. The .NET equivalent has to keep those first-class.

### Decision required
**What library do .NET services and workers use to run SQL and migrations against PostgreSQL?**

| 1 | **Npgsql + Dapper, plain-SQL migration files applied by a small shared migrator** | |
| :-: | :-- | :-- |
| | + The TDD's SQL is the code: every critical query is written exactly as specified, no translation layer<br>+ Npgsql array parameters make the 2,000-task insert one `INSERT … SELECT FROM unnest(...)` statement<br>+ Plain pooled connections compose with RDS Proxy (ADR-011) with no tuning<br>+ Migrations are ordered `.sql` files per service: reviewable DDL, partial/unique indexes verbatim | − No compile-time schema check: covered by integration tests that run every query against real Postgres |
| 2 | EF Core + Npgsql provider | |
| | + Typed LINQ, generated migrations | − `ExecuteUpdate` can't return rows: the claim, ingest and job-close queries drop to `FromSql` anyway, losing typing exactly where ADR-014 wanted it<br>− Change tracker overhead on 2,000-row insert |
| 3 | Raw ADO.NET only | |
| | + No dependency | − Hand-written row mapping everywhere |

### Decision
Option 1. Migrations: each service owns `migrations/NNN_name.sql` embedded in its assembly and applied
at startup under a `pg_advisory_lock` (two ECS tasks starting together can't race), recorded in a
`schema_migrations` table. Forward-only for v1, as the TDD launch plan states.

---

## ADR-019: Build orchestration (supersedes ADR-015 for backend code)

### Context
The repo now holds a .NET solution (shared lib, 3 services, worker projects, tests) and one npm
package (web).

### Decision
One `DocBridge.slnx` solution at the root with `Directory.Build.props` (target `net10.0`, nullable,
warnings as errors) and `Directory.Packages.props` (central package versions). `dotnet build`/`dotnet test`
resolve project-reference order natively, which is what ADR-015 wanted from "explicit root
scripts". The web client keeps npm, with its own `package.json`. No task runner, same reasoning as ADR-015.
