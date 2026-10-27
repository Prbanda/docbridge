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

output "api_url" {
  value = module.app.api_url
}

output "ws_url" {
  value = module.app.ws_url
}

output "cluster_name" {
  value = module.app.cluster_name
}

output "task_queue_url" {
  value = module.app.task_queue_url
}

output "lambda_functions" {
  value = module.app.lambda_functions
}

output "platform_admin_secret_arn" {
  value = module.app.platform_admin_secret_arn
}

output "delivery_worker_secret_arn" {
  value = module.app.delivery_worker_secret_arn
}

output "spa_distribution_id" {
  value = module.s3.spa_cloudfront_distribution_id
}
