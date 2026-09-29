# One PostgreSQL instance, three isolated logical databases (ADR-009/010),
# RDS Proxy for Lambda access (ADR-011), all credentials in Secrets Manager.
# The logical databases and roles themselves are created by the migration
# bootstrap job (one-off ECS task, wired in M1) — never by hand.

locals {
  name     = "${var.project}-${var.env}"
  services = ["auth", "platform", "docbridge"]
}

# --- Security groups -------------------------------------------------------

# Attach this SG to anything that needs database access (ECS services, the
# migration bootstrap task, Lambdas via the proxy).
resource "aws_security_group" "db_clients" {
  name        = "${local.name}-db-clients"
  description = "Marker SG for database clients"
  vpc_id      = var.vpc_id

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_security_group" "db" {
  name        = "${local.name}-db"
  description = "PostgreSQL instance"
  vpc_id      = var.vpc_id

  ingress {
    description     = "Postgres from clients and proxy"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.db_clients.id, aws_security_group.proxy.id]
  }
}

resource "aws_security_group" "proxy" {
  name        = "${local.name}-db-proxy"
  description = "RDS Proxy"
  vpc_id      = var.vpc_id

  ingress {
    description     = "Postgres from clients"
    from_port       = 5432
    to_port         = 5432
    protocol        = "tcp"
    security_groups = [aws_security_group.db_clients.id]
  }

  egress {
    from_port   = 5432
    to_port     = 5432
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

# --- Credentials (Secrets Manager, ADR-012 / requirements: Security) -------

resource "random_password" "master" {
  length  = 32
  special = false
}

resource "random_password" "service" {
  for_each = toset(local.services)
  length   = 32
  special  = false
}

resource "aws_secretsmanager_secret" "master" {
  name                    = "${var.project}/${var.env}/db/master"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "master" {
  secret_id = aws_secretsmanager_secret.master.id
  secret_string = jsonencode({
    username = "postgres"
    password = random_password.master.result
  })
}

resource "aws_secretsmanager_secret" "service" {
  for_each                = toset(local.services)
  name                    = "${var.project}/${var.env}/db/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "service" {
  for_each  = toset(local.services)
  secret_id = aws_secretsmanager_secret.service[each.key].id
  secret_string = jsonencode({
    username = "${each.key == "docbridge" ? "docbridge" : each.key}_svc"
    password = random_password.service[each.key].result
  })
}

# --- Instance ---------------------------------------------------------------

resource "aws_db_subnet_group" "this" {
  name       = local.name
  subnet_ids = var.private_subnet_ids
}

resource "aws_db_parameter_group" "this" {
  name   = local.name
  family = "postgres${split(".", var.engine_version)[0]}"

  # TLS everywhere (TDD Security & access). apply_method pinned to what AWS
  # records for this static parameter, avoiding a permanent plan diff.
  parameter {
    name         = "rds.force_ssl"
    value        = "1"
    apply_method = "pending-reboot"
  }
}

resource "aws_db_instance" "this" {
  identifier     = local.name
  engine         = "postgres"
  engine_version = var.engine_version
  instance_class = var.instance_class

  allocated_storage = var.allocated_storage_gb
  storage_type      = "gp3"
  storage_encrypted = true

  db_name  = "postgres"
  username = "postgres"
  password = random_password.master.result

  db_subnet_group_name   = aws_db_subnet_group.this.name
  vpc_security_group_ids = [aws_security_group.db.id]
  parameter_group_name   = aws_db_parameter_group.this.name
  publicly_accessible    = false
  multi_az               = var.multi_az

  backup_retention_period    = 7
  auto_minor_version_upgrade = true
  deletion_protection        = var.deletion_protection
  skip_final_snapshot        = !var.deletion_protection

  tags = { Name = local.name }
}

# --- RDS Proxy (required for Lambda workers, ADR-011) -----------------------

resource "aws_iam_role" "proxy" {
  name = "${local.name}-db-proxy"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "rds.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy" "proxy_secrets" {
  name = "read-db-secrets"
  role = aws_iam_role.proxy.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["secretsmanager:GetSecretValue"]
        Resource = concat([aws_secretsmanager_secret.master.arn], [for s in aws_secretsmanager_secret.service : s.arn])
      },
      {
        # The AWS-managed aws/secretsmanager key has no stable ARN to reference
        # before first use in a fresh account; ViaService scopes this safely.
        Effect   = "Allow"
        Action   = ["kms:Decrypt"]
        Resource = ["*"]
        Condition = {
          StringEquals = { "kms:ViaService" = "secretsmanager.${data.aws_region.current.region}.amazonaws.com" }
        }
      }
    ]
  })
}

data "aws_region" "current" {}

resource "aws_db_proxy" "this" {
  name                   = local.name
  engine_family          = "POSTGRESQL"
  role_arn               = aws_iam_role.proxy.arn
  vpc_subnet_ids         = var.private_subnet_ids
  vpc_security_group_ids = [aws_security_group.proxy.id]
  require_tls            = true

  dynamic "auth" {
    for_each = merge({ master = aws_secretsmanager_secret.master.arn }, { for k, s in aws_secretsmanager_secret.service : k => s.arn })
    content {
      auth_scheme = "SECRETS"
      iam_auth    = "DISABLED"
      secret_arn  = auth.value
    }
  }
}

resource "aws_db_proxy_default_target_group" "this" {
  db_proxy_name = aws_db_proxy.this.name

  connection_pool_config {
    max_connections_percent = 90
  }
}

resource "aws_db_proxy_target" "this" {
  db_proxy_name          = aws_db_proxy.this.name
  target_group_name      = aws_db_proxy_default_target_group.this.name
  db_instance_identifier = aws_db_instance.this.identifier
}
