output "vpc_id" {
  value = module.network.vpc_id
}

output "private_subnet_ids" {
  value = module.network.private_subnet_ids
}

output "db_endpoint" {
  value = module.rds.db_endpoint
}

output "db_proxy_endpoint" {
  value = module.rds.proxy_endpoint
}

output "db_clients_security_group_id" {
  value = module.rds.db_clients_security_group_id
}

output "staging_bucket_name" {
  value = module.s3.staging_bucket_name
}

output "spa_bucket_name" {
  value = module.s3.spa_bucket_name
}

output "spa_url" {
  value = "https://${module.s3.spa_cloudfront_domain}"
}
