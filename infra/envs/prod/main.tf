# Prod environment: same modules as dev, prod sizing. Applied only through
# the CI approval gate after M5 verification (launch plan).

locals {
  env = "prod"
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

  engine_version       = var.db_engine_version
  instance_class       = "db.t4g.small"
  multi_az             = true
  deletion_protection  = true
  allocated_storage_gb = 50
}

module "s3" {
  source = "../../modules/s3"

  env             = local.env
  spa_price_class = "PriceClass_100"
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

  desired_count = 2
  # TDD 2.4: reserved concurrency 50. Requires a Lambda concurrency quota increase above the default.
  delivery_max_concurrency      = 50
  delivery_reserved_concurrency = 50
  secret_recovery_window_days   = 7
}
