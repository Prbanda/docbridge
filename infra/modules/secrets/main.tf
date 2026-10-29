# Application secrets in Secrets Manager (requirements: Security). Services get them injected by ARN at
# runtime (ECS secrets / Lambda *_SECRET_ARN); no value ever appears in code or task definitions.
# Values are generated here, like the DB passwords in modules/rds, so they live only in Secrets Manager
# and the encrypted, private Terraform state bucket.

locals {
  name    = "${var.project}/${var.env}"
  clients = ["delivery-worker", "docbridge-api", "platform-admin"]
}

# RS256 signing key for the auth service (ADR-013 / TDD 1.1).
resource "tls_private_key" "jwt" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

resource "aws_secretsmanager_secret" "jwt" {
  name                    = "${local.name}/auth/jwt-private-key"
  recovery_window_in_days = var.recovery_window_days
}

resource "aws_secretsmanager_secret_version" "jwt" {
  secret_id     = aws_secretsmanager_secret.jwt.id
  secret_string = tls_private_key.jwt.private_key_pem_pkcs8 # gitleaks:allow (reference, not a value)
}

# Service-client secrets (client-credentials grant, ADR-013).
resource "random_password" "client" {
  for_each = toset(local.clients)
  length   = 40
  special  = false
}

resource "aws_secretsmanager_secret" "client" {
  for_each                = toset(local.clients)
  name                    = "${local.name}/clients/${each.key}"
  recovery_window_in_days = var.recovery_window_days
}

resource "aws_secretsmanager_secret_version" "client" {
  for_each      = toset(local.clients)
  secret_id     = aws_secretsmanager_secret.client[each.key].id
  secret_string = random_password.client[each.key].result
}
