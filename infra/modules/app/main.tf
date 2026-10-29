# The whole application tier on top of the M0 foundations (network, rds, s3), composed from the building-block
# modules. envs/dev and envs/prod both call this with different sizing, so the environments can't drift.

data "aws_region" "current" {}
data "aws_caller_identity" "current" {}

locals {
  name     = "${var.project}-${var.env}"
  region   = data.aws_region.current.region
  registry = "${data.aws_caller_identity.current.account_id}.dkr.ecr.${local.region}.amazonaws.com"
  image    = { for s in ["auth", "platform", "docbridge-api"] : s => "${local.registry}/${var.project}/${s}:${var.image_tag}" }
  pkg      = { for p in ["fanout", "delivery", "ws", "maintenance", "db-bootstrap"] : p => "${var.lambda_package_dir}/${p}.zip" }

  issuer    = module.http_api.api_endpoint # G1: tokens are issued for, and validated at, the public edge
  alb       = module.alb.internal_url
  db_secret = var.db.service_secret_arns["docbridge"]

  # Environment shared by every Lambda that touches the docbridge database through RDS Proxy (ADR-011).
  lambda_db_env = {
    DB_HOST          = var.db.proxy_endpoint
    DB_NAME          = "docbridge"
    DB_SECRET_ARN    = local.db_secret
    DB_MAX_POOL_SIZE = "2"
  }
  vpc_lambda = {
    subnet_ids         = var.private_subnet_ids
    security_group_ids = [var.db.clients_security_group_id]
  }
  manage_connections = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Action = ["execute-api:ManageConnections"], Resource = [module.websocket.manage_connections_arn] }]
  })
}

module "secrets" {
  source               = "../secrets"
  env                  = var.env
  recovery_window_days = var.secret_recovery_window_days
}

module "alb" {
  source             = "../alb"
  env                = var.env
  vpc_id             = var.vpc_id
  vpc_cidr           = var.vpc_cidr
  private_subnet_ids = var.private_subnet_ids
}

module "cluster" {
  source = "../ecs-cluster"
  env    = var.env
}

module "http_api" {
  source             = "../http-api"
  env                = var.env
  vpc_id             = var.vpc_id
  vpc_cidr           = var.vpc_cidr
  private_subnet_ids = var.private_subnet_ids
  alb_listener_arn   = module.alb.listener_arn
  cors_origins       = concat([var.spa_url], var.extra_cors_origins)
  issuer_ready       = module.auth.service_name
}

# --- Queues (ADR-005/008, TDD 2.3) -------------------------------------------------

module "job_queue" {
  source                     = "../sqs"
  env                        = var.env
  name                       = "job-queue"
  visibility_timeout_seconds = 120
}

module "task_queue" {
  source                     = "../sqs"
  env                        = var.env
  name                       = "task-queue"
  visibility_timeout_seconds = 6 * var.delivery_timeout_seconds # TDD: 6x the delivery timeout
}

# --- Database bootstrap (G11): logical DBs + roles, before any service starts --------

module "db_bootstrap" {
  source          = "../lambda"
  env             = var.env
  function        = "db-bootstrap"
  handler         = "DocBridge.DbBootstrap::DocBridge.DbBootstrap.Function::Handle"
  package_path    = local.pkg["db-bootstrap"]
  timeout_seconds = 60
  environment = {
    DB_HOST                 = var.db.endpoint
    MASTER_DB_SECRET_ARN    = var.db.master_secret_arn
    AUTH_DB_SECRET_ARN      = var.db.service_secret_arns["auth"]
    PLATFORM_DB_SECRET_ARN  = var.db.service_secret_arns["platform"]
    DOCBRIDGE_DB_SECRET_ARN = var.db.service_secret_arns["docbridge"]
  }
  secret_arns = concat([var.db.master_secret_arn], values(var.db.service_secret_arns))
  subnet_ids  = local.vpc_lambda.subnet_ids
  # db_clients SG is the one the RDS instance admits (modules/rds).
  security_group_ids = local.vpc_lambda.security_group_ids
}

resource "aws_lambda_invocation" "db_bootstrap" {
  function_name = module.db_bootstrap.function_name
  input         = jsonencode({ reason = "terraform" })
  triggers = {
    secrets = join(",", concat([var.db.master_secret_arn], values(var.db.service_secret_arns)))
    version = "1"
  }
}

# --- Services (ADR-006): ECS Fargate behind the internal ALB --------------------------

module "auth" {
  source                   = "../ecs-service"
  env                      = var.env
  service                  = "auth"
  image                    = local.image["auth"]
  port                     = 3001
  desired_count            = var.desired_count
  cluster_arn              = module.cluster.cluster_arn
  vpc_id                   = var.vpc_id
  private_subnet_ids       = var.private_subnet_ids
  alb_security_group_id    = module.alb.security_group_id
  extra_security_group_ids = [var.db.clients_security_group_id]
  listener_arn             = module.alb.listener_arn
  priority                 = 10
  path_patterns            = ["/auth/*", "/.well-known/*"]
  environment = {
    JWT_ISSUER   = local.issuer
    JWT_AUDIENCE = "docbridge"
    DB_HOST      = var.db.endpoint
    DB_NAME      = "auth"
  }
  secrets = {
    DB_USER                       = "${var.db.service_secret_arns["auth"]}:username::"
    DB_PASSWORD                   = "${var.db.service_secret_arns["auth"]}:password::"
    JWT_PRIVATE_KEY_PEM           = module.secrets.jwt_private_key_arn
    DELIVERY_WORKER_CLIENT_SECRET = module.secrets.client_secret_arns["delivery-worker"]
    DOCBRIDGE_API_CLIENT_SECRET   = module.secrets.client_secret_arns["docbridge-api"]
    PLATFORM_ADMIN_CLIENT_SECRET  = module.secrets.client_secret_arns["platform-admin"]
  }
  # Only the ECS service waits for the DB bootstrap (a module-level depends_on would defer every data source in
  # the module and replace the task definition on any upstream change).
  wait_for = aws_lambda_invocation.db_bootstrap.result
}

module "platform" {
  source                   = "../ecs-service"
  env                      = var.env
  service                  = "platform"
  image                    = local.image["platform"]
  port                     = 3002
  cpu                      = 512
  memory                   = 1024
  desired_count            = var.desired_count
  cluster_arn              = module.cluster.cluster_arn
  vpc_id                   = var.vpc_id
  private_subnet_ids       = var.private_subnet_ids
  alb_security_group_id    = module.alb.security_group_id
  extra_security_group_ids = [var.db.clients_security_group_id]
  listener_arn             = module.alb.listener_arn
  priority                 = 20
  path_patterns            = ["/teams*", "/binders*", "/documents*", "/users/*", "/admin/*", "/search"]
  ephemeral_storage_gib    = 30 # 1 GB ingest temp files
  environment = {
    JWT_ISSUER      = local.issuer
    JWT_AUDIENCE    = "docbridge"
    AUTH_JWKS_URL   = "${local.alb}/.well-known/jwks.json"
    PLATFORM_BUCKET = var.platform_bucket.name
    DB_HOST         = var.db.endpoint
    DB_NAME         = "platform"
    # ~11 parallel membership queries per submission (see packages/shared/Db/Database.cs WarmPoolAsync).
    DB_MIN_POOL_SIZE = "10"
  }
  secrets = {
    DB_USER     = "${var.db.service_secret_arns["platform"]}:username::"
    DB_PASSWORD = "${var.db.service_secret_arns["platform"]}:password::"
  }
  task_policy_json = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect   = "Allow"
      Action   = ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"]
      Resource = ["${var.platform_bucket.arn}/*"]
    }]
  })
  # Only the ECS service waits for the DB bootstrap (a module-level depends_on would defer every data source in
  # the module and replace the task definition on any upstream change).
  wait_for = aws_lambda_invocation.db_bootstrap.result
}

module "docbridge_api" {
  source  = "../ecs-service"
  env     = var.env
  service = "docbridge-api"
  image   = local.image["docbridge-api"]
  port    = 3003
  # Measured: at 0.25 vCPU a 2,000-task submission's encode/insert work was CPU-starved (1-min peaks to 92 %),
  # pushing p99 past 500 ms. 0.5 vCPU, like the platform.
  cpu                      = 512
  memory                   = 1024
  desired_count            = var.desired_count
  cluster_arn              = module.cluster.cluster_arn
  vpc_id                   = var.vpc_id
  private_subnet_ids       = var.private_subnet_ids
  alb_security_group_id    = module.alb.security_group_id
  extra_security_group_ids = [var.db.clients_security_group_id]
  listener_arn             = module.alb.listener_arn
  priority                 = 30
  path_patterns            = ["/destinations*", "/files*", "/jobs*"]
  environment = {
    JWT_ISSUER       = local.issuer
    JWT_AUDIENCE     = "docbridge"
    AUTH_URL         = local.alb
    PLATFORM_URL     = local.alb
    STAGING_BUCKET   = var.staging_bucket.name
    JOB_QUEUE_URL    = module.job_queue.queue_url
    DB_HOST          = var.db.endpoint
    DB_NAME          = "docbridge"
    DB_MIN_POOL_SIZE = "5"
  }
  secrets = {
    DB_USER                     = "${local.db_secret}:username::"
    DB_PASSWORD                 = "${local.db_secret}:password::"
    DOCBRIDGE_API_CLIENT_SECRET = module.secrets.client_secret_arns["docbridge-api"]
  }
  # Presigning signs with the task role, so the role must hold the permissions the URLs grant.
  task_policy_json = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["s3:PutObject", "s3:GetObject", "s3:DeleteObject", "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts"]
        Resource = ["${var.staging_bucket.arn}/staging/*"]
      },
      { Effect = "Allow", Action = ["sqs:SendMessage"], Resource = [module.job_queue.queue_arn] },
      # Job page file status: a missing staged object answers 404 (not 403) with ListBucket.
      {
        Effect    = "Allow"
        Action    = ["s3:ListBucket"]
        Resource  = [var.staging_bucket.arn]
        Condition = { StringLike = { "s3:prefix" = ["staging/*"] } }
      },
    ]
  })
  # Only the ECS service waits for the DB bootstrap (a module-level depends_on would defer every data source in
  # the module and replace the task definition on any upstream change).
  wait_for = aws_lambda_invocation.db_bootstrap.result
}

# --- Workers (ADR-011): Lambda, SQS-triggered ------------------------------------------

module "fanout" {
  source              = "../lambda"
  env                 = var.env
  function            = "fanout"
  handler             = "DocBridge.Worker.Fanout::DocBridge.Worker.Fanout.Function::Handle"
  package_path        = local.pkg["fanout"]
  timeout_seconds     = 60
  environment         = merge(local.lambda_db_env, { TASK_QUEUE_URL = module.task_queue.queue_url })
  secret_arns         = [local.db_secret]
  subnet_ids          = local.vpc_lambda.subnet_ids
  security_group_ids  = local.vpc_lambda.security_group_ids
  sqs_trigger_arn     = module.job_queue.queue_arn
  sqs_trigger_enabled = true
  max_concurrency     = 2
  policy_json = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Action = ["sqs:SendMessage"], Resource = [module.task_queue.queue_arn] }]
  })
}

module "delivery" {
  source               = "../lambda"
  env                  = var.env
  function             = "delivery"
  handler              = "DocBridge.Worker.Delivery::DocBridge.Worker.Delivery.Function::Handle"
  package_path         = local.pkg["delivery"]
  memory_mb            = 1024 # memory = network throughput for the 1 GB worst case (ADR-011)
  timeout_seconds      = var.delivery_timeout_seconds
  reserved_concurrency = var.delivery_reserved_concurrency
  environment = merge(local.lambda_db_env, {
    STAGING_BUCKET                           = var.staging_bucket.name
    AUTH_URL                                 = local.alb
    PLATFORM_URL                             = local.alb
    TASK_QUEUE_URL                           = module.task_queue.queue_url
    WS_CALLBACK_URL                          = module.websocket.callback_url
    DELIVERY_WORKER_CLIENT_SECRET_SECRET_ARN = module.secrets.client_secret_arns["delivery-worker"]
    RETRY_BACKOFF_SECONDS                    = var.retry_backoff_seconds
    DELIVERY_HTTP_TIMEOUT_SECONDS            = tostring(var.delivery_timeout_seconds - 30)
  })
  secret_arns         = [local.db_secret, module.secrets.client_secret_arns["delivery-worker"]]
  subnet_ids          = local.vpc_lambda.subnet_ids
  security_group_ids  = local.vpc_lambda.security_group_ids
  sqs_trigger_arn     = module.task_queue.queue_arn
  sqs_trigger_enabled = true
  # Concurrency cap: keeps RDS Proxy connections bounded during a 2,000-task burst (TDD 2.4).
  max_concurrency = var.delivery_max_concurrency
  policy_json = jsonencode({
    Version = "2012-10-17"
    Statement = [
      { Effect = "Allow", Action = ["s3:GetObject"], Resource = ["${var.staging_bucket.arn}/staging/*"] },
      # Without ListBucket S3 answers HEAD on a missing key with 403 instead of 404, and a never-uploaded file
      # would be retried as transient instead of failing as FILE_NOT_UPLOADED.
      {
        Effect    = "Allow"
        Action    = ["s3:ListBucket"]
        Resource  = [var.staging_bucket.arn]
        Condition = { StringLike = { "s3:prefix" = ["staging/*"] } }
      },
      { Effect = "Allow", Action = ["execute-api:ManageConnections"], Resource = [module.websocket.manage_connections_arn] },
    ]
  })
}

module "ws_handlers" {
  source   = "../lambda"
  for_each = { connect = "Connect", subscribe = "Subscribe", disconnect = "Disconnect" }

  env             = var.env
  function        = "ws-${each.key}"
  handler         = "DocBridge.Worker.Ws::DocBridge.Worker.Ws.Function::${each.value}"
  package_path    = local.pkg["ws"]
  timeout_seconds = 15
  environment = merge(local.lambda_db_env, {
    JWT_ISSUER                             = local.issuer
    JWT_AUDIENCE                           = "docbridge"
    AUTH_URL                               = local.alb
    PLATFORM_URL                           = local.alb
    WS_CALLBACK_URL                        = module.websocket.callback_url
    DOCBRIDGE_API_CLIENT_SECRET_SECRET_ARN = module.secrets.client_secret_arns["docbridge-api"]
  })
  secret_arns        = [local.db_secret, module.secrets.client_secret_arns["docbridge-api"]]
  subnet_ids         = local.vpc_lambda.subnet_ids
  security_group_ids = local.vpc_lambda.security_group_ids
  policy_json        = local.manage_connections
}

module "websocket" {
  source              = "../websocket-api"
  env                 = var.env
  connect_function    = { function_name = module.ws_handlers["connect"].function_name, invoke_arn = module.ws_handlers["connect"].invoke_arn }
  subscribe_function  = { function_name = module.ws_handlers["subscribe"].function_name, invoke_arn = module.ws_handlers["subscribe"].invoke_arn }
  disconnect_function = { function_name = module.ws_handlers["disconnect"].function_name, invoke_arn = module.ws_handlers["disconnect"].invoke_arn }
}

# --- DLQ consumers + sweeper (TDD 2.5) ----------------------------------------------------

module "task_dlq" {
  source              = "../lambda"
  env                 = var.env
  function            = "task-dlq"
  handler             = "DocBridge.Worker.Maintenance::DocBridge.Worker.Maintenance.Function::TaskDlq"
  package_path        = local.pkg["maintenance"]
  environment         = merge(local.lambda_db_env, { WS_CALLBACK_URL = module.websocket.callback_url })
  secret_arns         = [local.db_secret]
  subnet_ids          = local.vpc_lambda.subnet_ids
  security_group_ids  = local.vpc_lambda.security_group_ids
  sqs_trigger_arn     = module.task_queue.dlq_arn
  sqs_trigger_enabled = true
  max_concurrency     = 2
  policy_json         = local.manage_connections
}

module "job_dlq" {
  source              = "../lambda"
  env                 = var.env
  function            = "job-dlq"
  handler             = "DocBridge.Worker.Maintenance::DocBridge.Worker.Maintenance.Function::JobDlq"
  package_path        = local.pkg["maintenance"]
  environment         = merge(local.lambda_db_env, { WS_CALLBACK_URL = module.websocket.callback_url })
  secret_arns         = [local.db_secret]
  subnet_ids          = local.vpc_lambda.subnet_ids
  security_group_ids  = local.vpc_lambda.security_group_ids
  sqs_trigger_arn     = module.job_queue.dlq_arn
  sqs_trigger_enabled = true
  max_concurrency     = 2
  policy_json         = local.manage_connections
}

module "sweeper" {
  source              = "../lambda"
  env                 = var.env
  function            = "sweeper"
  handler             = "DocBridge.Worker.Maintenance::DocBridge.Worker.Maintenance.Function::Sweep"
  package_path        = local.pkg["maintenance"]
  timeout_seconds     = 60
  environment         = merge(local.lambda_db_env, { JOB_QUEUE_URL = module.job_queue.queue_url })
  secret_arns         = [local.db_secret]
  subnet_ids          = local.vpc_lambda.subnet_ids
  security_group_ids  = local.vpc_lambda.security_group_ids
  schedule_expression = "rate(15 minutes)"
  policy_json = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Action = ["sqs:SendMessage"], Resource = [module.job_queue.queue_arn] }]
  })
}

# Blueprint success metric "DLQ depth stays at 0" (TDD launch plan: two cheap alarms).
resource "aws_cloudwatch_metric_alarm" "dlq_depth" {
  for_each            = { task = module.task_queue.dlq_name, job = module.job_queue.dlq_name }
  alarm_name          = "${local.name}-${each.key}-dlq-not-empty"
  namespace           = "AWS/SQS"
  metric_name         = "ApproximateNumberOfMessagesVisible"
  dimensions          = { QueueName = each.value }
  statistic           = "Maximum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}

resource "aws_cloudwatch_metric_alarm" "delivery_errors" {
  alarm_name          = "${local.name}-delivery-errors"
  namespace           = "AWS/Lambda"
  metric_name         = "Errors"
  dimensions          = { FunctionName = module.delivery.function_name }
  statistic           = "Sum"
  period              = 300
  evaluation_periods  = 1
  threshold           = 5
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}
