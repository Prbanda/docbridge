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

  engine_version       = var.db_engine_version
  instance_class       = "db.t4g.micro"
  multi_az             = false
  deletion_protection  = false
  allocated_storage_gb = 20
}

module "s3" {
  source = "../../modules/s3"

  env             = local.env
  spa_price_class = "PriceClass_100"
  force_destroy   = true # dev is disposable: terraform destroy must always succeed
}
