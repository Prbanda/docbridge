output "api_url" {
  value = module.http_api.api_endpoint
}

output "ws_url" {
  value = module.websocket.ws_url
}

output "cluster_name" {
  value = module.cluster.cluster_name
}

output "job_queue_url" {
  value = module.job_queue.queue_url
}

output "task_queue_url" {
  value = module.task_queue.queue_url
}

output "task_dlq_url" {
  value = module.task_queue.dlq_url
}

output "job_dlq_url" {
  value = module.job_queue.dlq_url
}

output "lambda_functions" {
  description = "Function name per package, for update-function-code."
  value = {
    fanout         = [module.fanout.function_name]
    delivery       = [module.delivery.function_name]
    ws             = [for m in module.ws_handlers : m.function_name]
    maintenance    = [module.task_dlq.function_name, module.job_dlq.function_name, module.sweeper.function_name]
    "db-bootstrap" = [module.db_bootstrap.function_name]
  }
}

output "platform_admin_secret_arn" {
  value = module.secrets.client_secret_arns["platform-admin"]
}

output "delivery_worker_secret_arn" {
  value = module.secrets.client_secret_arns["delivery-worker"]
}
