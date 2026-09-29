output "vpc_id" {
  value = module.network.vpc_id
}

output "db_proxy_endpoint" {
  value = module.rds.proxy_endpoint
}

output "staging_bucket_name" {
  value = module.s3.staging_bucket_name
}

output "spa_url" {
  value = "https://${module.s3.spa_cloudfront_domain}"
}
