# Dev environment: thin config only — every resource block lives in
# infra/modules/* (TDD repository rule).
# CI: path filter on infra/** — touch this file to re-run apply-dev.

locals {
  env = "dev"
}

module "network" {
  source = "../../modules/network"

  env = local.env
}

module "rds" {
  source = "../../modules/rds"

  env                = local.env
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids

  engine_version = var.db_engine_version
  # Same class as prod: on db.t4g.micro (1 GB) the grown task tables stopped fitting in cache and the 2,000-row
  # submission insert alone took 150–450 ms, breaking the 500 ms p99 gate (M5-TESTING §5.3).
  instance_class       = "db.t4g.small"
  multi_az             = false
  deletion_protection  = false
  apply_immediately    = true
  allocated_storage_gb = 20
}

module "s3" {
  source = "../../modules/s3"

  env             = local.env
  spa_price_class = "PriceClass_100"
  force_destroy   = true # dev is disposable: terraform destroy must always succeed
}

# Application tier (M1-M5): services, workers, queues, edge. Same module in every environment.
module "app" {
  source = "../../modules/app"

  env                = local.env
  vpc_id             = module.network.vpc_id
  vpc_cidr           = module.network.vpc_cidr
  private_subnet_ids = module.network.private_subnet_ids

  db = {
    endpoint                  = module.rds.db_endpoint
    proxy_endpoint            = module.rds.proxy_endpoint
    clients_security_group_id = module.rds.db_clients_security_group_id
    master_secret_arn         = module.rds.master_secret_arn
    service_secret_arns       = module.rds.service_secret_arns
  }
  staging_bucket  = { name = module.s3.staging_bucket_name, arn = module.s3.staging_bucket_arn }
  platform_bucket = { name = module.s3.platform_bucket_name, arn = module.s3.platform_bucket_arn }
  spa_url         = "https://${module.s3.spa_cloudfront_domain}"

  image_tag          = local.env
  lambda_package_dir = "${path.root}/../../../dist/lambda"

  desired_count      = 2
  extra_cors_origins = ["http://localhost:5173"]
  # Account Lambda quota is 10 concurrent executions (all must stay unreserved), so dev caps delivery
  # through the SQS trigger instead of TDD 2.4's reserved concurrency of 50.
  delivery_max_concurrency      = 5
  delivery_reserved_concurrency = -1
}
