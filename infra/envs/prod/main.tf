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
