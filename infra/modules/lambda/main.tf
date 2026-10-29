# One .NET 10 Lambda function (ADR-011/017) with its role, log group, optional VPC attachment,
# optional SQS trigger (partial batch responses) and optional EventBridge schedule.
#
# Code: Terraform creates the function from the package built by scripts/build-lambdas.sh. After that the
# workers CI workflow ships new code with update-function-code, so code changes are ignored here.

data "aws_region" "current" {}
data "aws_caller_identity" "current" {}

locals {
  name         = "${var.project}-${var.env}-${var.function}"
  has_package  = fileexists(var.package_path)
  package_path = local.has_package ? var.package_path : data.archive_file.placeholder.output_path
}

# Lets `terraform validate/plan` run where packages weren't built (e.g. a fresh CI checkout).
data "archive_file" "placeholder" {
  type        = "zip"
  output_path = "${path.module}/.placeholder-${var.function}.zip"
  source {
    content  = "placeholder: build with scripts/build-lambdas.sh"
    filename = "README.txt"
  }
}

resource "aws_cloudwatch_log_group" "this" {
  name              = "/aws/lambda/${local.name}"
  retention_in_days = var.log_retention_days
}

resource "aws_iam_role" "this" {
  name = local.name
  assume_role_policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Principal = { Service = "lambda.amazonaws.com" }, Action = "sts:AssumeRole" }]
  })
}

resource "aws_iam_role_policy_attachment" "basic" {
  role       = aws_iam_role.this.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

resource "aws_iam_role_policy_attachment" "vpc" {
  count      = length(var.subnet_ids) > 0 ? 1 : 0
  role       = aws_iam_role.this.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
}

resource "aws_iam_role_policy" "app" {
  count  = var.policy_json == null ? 0 : 1
  name   = "app"
  role   = aws_iam_role.this.id
  policy = var.policy_json
}

resource "aws_iam_role_policy" "secrets" {
  count = length(var.secret_arns) > 0 ? 1 : 0
  name  = "read-own-secrets"
  role  = aws_iam_role.this.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = var.secret_arns },
      {
        Effect    = "Allow"
        Action    = ["kms:Decrypt"]
        Resource  = ["*"]
        Condition = { StringEquals = { "kms:ViaService" = "secretsmanager.${data.aws_region.current.region}.amazonaws.com" } }
      },
    ]
  })
}

resource "aws_iam_role_policy" "sqs_trigger" {
  count = var.sqs_trigger_enabled ? 1 : 0
  name  = "consume-trigger-queue"
  role  = aws_iam_role.this.id
  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes", "sqs:ChangeMessageVisibility"]
      Resource = [var.sqs_trigger_arn]
    }]
  })
}

# IAM is eventually consistent: a VPC function created the moment its role's policies attach can fail ENI
# creation with InsufficientRolePermissions and land in the Failed state.
resource "time_sleep" "iam_propagation" {
  create_duration = "15s"
  depends_on = [
    aws_iam_role_policy_attachment.basic,
    aws_iam_role_policy_attachment.vpc,
    aws_iam_role_policy.app,
    aws_iam_role_policy.secrets,
    aws_iam_role_policy.sqs_trigger,
  ]
}

resource "aws_lambda_function" "this" {
  function_name    = local.name
  role             = aws_iam_role.this.arn
  runtime          = "dotnet10"
  architectures    = ["x86_64"]
  handler          = var.handler
  filename         = local.package_path
  source_code_hash = filebase64sha256(local.package_path)
  memory_size      = var.memory_mb
  timeout          = var.timeout_seconds

  reserved_concurrent_executions = var.reserved_concurrency

  environment {
    variables = var.environment
  }

  dynamic "vpc_config" {
    for_each = length(var.subnet_ids) > 0 ? [1] : []
    content {
      subnet_ids         = var.subnet_ids
      security_group_ids = var.security_group_ids
    }
  }

  lifecycle {
    ignore_changes = [filename, source_code_hash]
  }

  depends_on = [aws_cloudwatch_log_group.this, time_sleep.iam_propagation]
}

resource "aws_lambda_event_source_mapping" "sqs" {
  count                   = var.sqs_trigger_enabled ? 1 : 0
  event_source_arn        = var.sqs_trigger_arn
  function_name           = aws_lambda_function.this.arn
  batch_size              = var.batch_size
  function_response_types = ["ReportBatchItemFailures"]

  dynamic "scaling_config" {
    for_each = var.max_concurrency == null ? [] : [1]
    content {
      maximum_concurrency = var.max_concurrency
    }
  }

  depends_on = [aws_iam_role_policy.sqs_trigger]
}

resource "aws_cloudwatch_event_rule" "schedule" {
  count               = var.schedule_expression == null ? 0 : 1
  name                = local.name
  schedule_expression = var.schedule_expression
}

resource "aws_cloudwatch_event_target" "schedule" {
  count = var.schedule_expression == null ? 0 : 1
  rule  = aws_cloudwatch_event_rule.schedule[0].name
  arn   = aws_lambda_function.this.arn
}

resource "aws_lambda_permission" "schedule" {
  count         = var.schedule_expression == null ? 0 : 1
  statement_id  = "AllowEventBridge"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.this.function_name
  principal     = "events.amazonaws.com"
  source_arn    = aws_cloudwatch_event_rule.schedule[0].arn
}
