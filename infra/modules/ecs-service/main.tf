# One ECS Fargate service behind the internal ALB: task definition, IAM roles, target group, path rule.
# Secrets are referenced by ARN and injected at container start; values never appear here.

data "aws_region" "current" {}

locals {
  name        = "${var.project}-${var.env}-${var.service}"
  secret_arns = distinct([for v in values(var.secrets) : split(":password::", split(":username::", v)[0])[0]])
}

resource "aws_cloudwatch_log_group" "this" {
  name              = "/ecs/${local.name}"
  retention_in_days = var.log_retention_days
}

# --- IAM ---------------------------------------------------------------------

data "aws_iam_policy_document" "ecs_assume" {
  statement {
    actions = ["sts:AssumeRole"]
    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }
  }
}

# Execution role: pull the image, write logs, read exactly this service's secrets.
resource "aws_iam_role" "execution" {
  name               = "${local.name}-exec"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
}

resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}

resource "aws_iam_role_policy" "execution_secrets" {
  count = length(var.secrets) > 0 ? 1 : 0
  name  = "read-own-secrets"
  role  = aws_iam_role.execution.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = local.secret_arns },
      {
        Effect    = "Allow"
        Action    = ["kms:Decrypt"]
        Resource  = ["*"]
        Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${data.aws_region.current.region}.amazonaws.com" } }
      },
    ]
  })
}

# Task role: what the application itself may call (S3, SQS, …), passed in per service.
resource "aws_iam_role" "task" {
  name               = "${local.name}-task"
  assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
}

resource "aws_iam_role_policy" "task" {
  count  = var.task_policy_json == null ? 0 : 1
  name   = "app"
  role   = aws_iam_role.task.id
  policy = var.task_policy_json
}

# --- Networking ----------------------------------------------------------------

resource "aws_security_group" "service" {
  name        = local.name
  description = "${var.service} tasks"
  vpc_id      = var.vpc_id

  ingress {
    description     = "From the internal ALB"
    from_port       = var.port
    to_port         = var.port
    protocol        = "tcp"
    security_groups = [var.alb_security_group_id]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}

resource "aws_lb_target_group" "this" {
  name        = substr("${var.env}-${var.service}", 0, 32)
  port        = var.port
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = var.vpc_id

  deregistration_delay = 30

  health_check {
    path                = "/health"
    matcher             = "200"
    interval            = 15
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

# An ALB rule holds at most 5 path patterns, so the patterns are split across consecutive priorities.
resource "aws_lb_listener_rule" "this" {
  for_each     = { for i, chunk in chunklist(var.path_patterns, 5) : tostring(i) => chunk }
  listener_arn = var.listener_arn
  priority     = var.priority + tonumber(each.key)

  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.this.arn
  }

  condition {
    path_pattern {
      values = each.value
    }
  }
}

moved {
  from = aws_lb_listener_rule.this
  to   = aws_lb_listener_rule.this["0"]
}

# --- Task + service ------------------------------------------------------------

resource "aws_ecs_task_definition" "this" {
  family                   = local.name
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = var.cpu
  memory                   = var.memory
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }

  # 1 GB ingest temp files (platform) need more than the 20 GiB default only if many run at once.
  ephemeral_storage {
    size_in_gib = var.ephemeral_storage_gib
  }

  container_definitions = jsonencode([{
    name         = var.service
    image        = var.image
    essential    = true
    portMappings = [{ containerPort = var.port, protocol = "tcp" }]
    environment  = [for k, v in merge(var.environment, { PORT = tostring(var.port) }) : { name = k, value = v }]
    secrets      = [for k, v in var.secrets : { name = k, valueFrom = v }]
    healthCheck = {
      command     = ["CMD-SHELL", "wget -qO- http://127.0.0.1:${var.port}/health || exit 1"]
      interval    = 15
      timeout     = 5
      retries     = 3
      startPeriod = 30
    }
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.this.name
        awslogs-region        = data.aws_region.current.region
        awslogs-stream-prefix = var.service
      }
    }
  }])
}

resource "terraform_data" "wait_for" {
  input = var.wait_for
}

resource "aws_ecs_service" "this" {
  name            = var.service
  cluster         = var.cluster_arn
  task_definition = aws_ecs_task_definition.this.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  # Two tasks spread across AZs (TDD 1.1 / failure modes: one AZ down).
  network_configuration {
    subnets          = var.private_subnet_ids
    security_groups  = concat([aws_security_group.service.id], var.extra_security_group_ids)
    assign_public_ip = false
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.this.arn
    container_name   = var.service
    container_port   = var.port
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  deployment_minimum_healthy_percent = 50
  deployment_maximum_percent         = 200
  health_check_grace_period_seconds  = 60

  # Apply returns only once tasks pass health checks: the gateway authorizer needs auth serving its discovery doc.
  wait_for_steady_state = true

  depends_on = [aws_lb_listener_rule.this, terraform_data.wait_for]
}
