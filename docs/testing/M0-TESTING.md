# M0 Testing — Foundations

**Milestone claim:** local environment runs via docker compose, Terraform provisions the dev foundations, CI pipelines are green.
**Already self-validated by the agent (SV-1/SV-2):** typecheck, lint, 24 unit tests across 8 files, `terraform fmt` + `terraform validate` on all three environments (AWS provider ~> 6.0), workflow YAML parsing, gitleaks scan (no leaks), all three Lambda bundles build via esbuild.
**Not self-validatable, verified by you here:** docker compose runtime (no Docker on the agent machine), everything that touches a real AWS account.

---

## 1. Local verification

Prerequisites (once): Docker Desktop (or OrbStack), Node ≥22.

```bash
# 1. Install and test — should end with all workspaces passing
npm ci
npm run typecheck && npm run lint && npm test

# 2. Local environment defaults
cp .env.example .env

# 3. Bring everything up (first run builds 3 images, ~2-4 min)
docker compose up --build -d

# 4. Wait until all containers report healthy
docker compose ps

# 5. Run the smoke test
./scripts/smoke-local.sh
```

**Expected smoke output — every line PASS:**

- `auth|platform|docbridge-api /health` — the three service containers are up.
- `... /health/ready` — each service reaches **its own** logical database with **its own** credentials.
- `auth_svc denied on platform db` — cross-service database access is impossible by construction (ADR-009).
- `staging bucket exists`, four `queue ... exists` lines, `redrive maxReceiveCount=3` — LocalStack mirrors the AWS topology (ADR-005/008).

Optional: run a worker as a local process (TDD 0.1) and see it consume a message:

```bash
# Terminal 1
JOB_QUEUE_URL=http://localhost:4566/000000000000/docbridge-job-queue \
AWS_ENDPOINT_URL=http://localhost:4566 AWS_ACCESS_KEY_ID=test AWS_SECRET_ACCESS_KEY=test \
npm run dev -w @docbridge/worker-fanout

# Terminal 2 — expect Terminal 1 to log: fanout stub received job message
docker compose exec localstack awslocal sqs send-message \
  --queue-url http://localhost:4566/000000000000/docbridge-job-queue \
  --message-body '{"jobId":"smoke-test"}'
```

Teardown: `docker compose down` (add `-v` to reset the databases).

---

## 2. Deploy to AWS (dev) — full setup runbook

Everything below is once-per-account except step 6.

### 2.1 Account and tooling

1. AWS account with a billing alarm (Billing → Budgets → e.g. $50/month alert).
2. Install tooling: `aws` CLI v2, `terraform` ≥1.15.8, `docker`.
3. `aws configure` (or SSO) with an admin-capable identity. Region: **us-east-1**.
4. Sanity: `aws sts get-caller-identity` returns your account.

### 2.2 Terraform state backend (the single manual step, ADR-012)

```bash
./bootstrap/create-state-backend.sh
```

Copy the printed values into `infra/envs/{global,dev,prod}/backend.hcl` (examples sit next to each as `backend.hcl.example` — only the `key` differs per env).

### 2.3 GitHub OIDC role for CI (no long-lived AWS keys)

Create the GitHub OIDC provider + a deploy role (console-free, one-time — replace `<ACCOUNT_ID>`, `<GITHUB_ORG>/<REPO>`):

```bash
aws iam create-open-id-connect-provider \
  --url https://token.actions.githubusercontent.com \
  --client-id-list sts.amazonaws.com

cat > /tmp/trust.json <<'EOF'
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::<ACCOUNT_ID>:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": { "token.actions.githubusercontent.com:aud": "sts.amazonaws.com" },
      "StringLike": { "token.actions.githubusercontent.com:sub": "repo:<GITHUB_ORG>/<REPO>:*" }
    }
  }]
}
EOF
aws iam create-role --role-name docbridge-github-deploy \
  --assume-role-policy-document file:///tmp/trust.json
# v1 scope note: AdministratorAccess keeps M0 simple; tightened before prod.
aws iam attach-role-policy --role-name docbridge-github-deploy \
  --policy-arn arn:aws:iam::aws:policy/AdministratorAccess
```

### 2.4 GitHub repository wiring

1. Push this repo to GitHub (`main` branch).
2. Settings → Secrets and variables → Actions → **Variables**, add:
   - `AWS_DEPLOY_ROLE_ARN` = `arn:aws:iam::<ACCOUNT_ID>:role/docbridge-github-deploy`
   - `AWS_REGION` = `us-east-1`
   - `TF_STATE_BUCKET` = from step 2.2
   - (leave `ECS_DEPLOY_ENABLED` / `LAMBDA_DEPLOY_ENABLED` unset until M1/M2)
3. Settings → Environments: create `dev` (no protection) and `prod` (required reviewer: you). The `prod` environment is the manual approval gate.

### 2.5 First deploy

Either merge to `main` and let the `infra` workflow apply global + dev, or run locally:

```bash
cd infra/envs/global
terraform init -backend-config=backend.hcl && terraform apply   # ECR repos

cd ../dev
terraform init -backend-config=backend.hcl && terraform apply   # ~20-30 min (RDS, NAT, CloudFront)
```

**Assumption A4 status: verified 2026-07-24, target health re-verified
2026-07-29.** The apply provisioned Postgres 17.9 and
`describe-db-proxy-targets` reports `TargetHealth.State: AVAILABLE` — the
full proxy→instance auth path works, not just registration (check §3.5).
If AWS later announces proxy support for a newer major, bump
`db_engine_version` in `infra/envs/*/variables.tf` and `POSTGRES_VERSION`
in `.env` together.

### 2.6 Known M0 limitation (by design)

The three **logical databases and roles inside RDS** are created by the migration
bootstrap job, which runs as a one-off ECS task — and the ECS cluster arrives in
M1. At the end of M0 the RDS instance is up with all credentials in Secrets
Manager, but the `auth`/`platform`/`docbridge` databases don't exist yet. This is
the TDD's own sequencing (ECS services are an M1 deliverable); M1-TESTING will
include the bootstrap-task run and its verification.

---

## 3. Verify in AWS (dev)

Console is read-only; all checks via CLI.

```bash
cd infra/envs/dev

# 3.1 Terraform state is clean: expect "No changes" on a fresh plan
terraform plan

# 3.2 Outputs exist
terraform output    # vpc_id, db_endpoint, db_proxy_endpoint, staging_bucket_name, spa_url

# 3.3 RDS is private, encrypted, TLS-forced
aws rds describe-db-instances --db-instance-identifier docbridge-dev \
  --query 'DBInstances[0].{public:PubliclyAccessible,encrypted:StorageEncrypted,status:DBInstanceStatus}'
# expect: public=false, encrypted=true, status=available

# 3.4 Staging bucket: SSE-KMS + BucketKey, public access blocked, lifecycle 30d/7d
BUCKET=$(terraform output -raw staging_bucket_name)
aws s3api get-bucket-encryption --bucket "$BUCKET"
aws s3api get-public-access-block --bucket "$BUCKET"
aws s3api get-bucket-lifecycle-configuration --bucket "$BUCKET"

# 3.5 RDS Proxy target is healthy (proxy Status=available alone is not enough:
#     a proxy can be available while its target is unhealthy, e.g. bad secret)
aws rds describe-db-proxy-targets --db-proxy-name docbridge-dev \
  --query 'Targets[0].TargetHealth.State'
# expect: "AVAILABLE"

# 3.6 TLS-only policy holds. An anonymous plain-HTTP curl proves nothing (it
#     gets AccessDenied over HTTPS too). Make an AUTHENTICATED request that
#     succeeds over HTTPS, then repeat it over plain HTTP and expect an
#     explicit deny from the bucket policy (the aws:SecureTransport condition):
aws s3 ls "s3://${BUCKET}"                                        # succeeds
aws s3 ls "s3://${BUCKET}" --endpoint-url http://s3.amazonaws.com # fails
# expect: "...explicit deny in a resource-based policy"

# 3.7 SPA CloudFront answers (empty bucket → index.html 403-mapped; page loads after M1 ships the SPA)
curl -sI "$(terraform output -raw spa_url)" | head -1

# 3.8 CI green: push a trivial change to main and confirm all workflows pass,
#     including gitleaks and the infra plan/apply
```

**M0 exit criteria:** smoke script fully green locally; `terraform plan` clean in dev; RDS private+encrypted; staging bucket encrypted with lifecycle rules; CI workflows green on `main`.

---

## 4. Teardown — stop paying between sessions

```bash
./scripts/aws-down.sh          # destroys the dev environment (asks for confirmation)
./scripts/aws-down.sh --all    # also destroys the global ECR repos
./scripts/aws-up.sh            # recreates everything (~20-30 min)
```

What stays after a plain teardown and what it costs: the Terraform state
bucket (S3-native lockfiles, ~pennies/month) and the ECR repos (image storage,
$0.10/GB/month — cents at our image sizes). Everything metered by the hour —
NAT gateway, RDS instance, RDS Proxy, VPC, CloudFront — is destroyed.
Secrets are force-deleted (`recovery_window_in_days = 0`) so a same-week
`aws-up.sh` recreates them without a name-collision error.
Dev data (users, jobs, staged files) is lost on teardown by design; it's a
disposable environment and M1+ seed scripts repopulate it.
