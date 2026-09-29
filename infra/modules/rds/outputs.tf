output "db_endpoint" {
  value = aws_db_instance.this.address
}

output "proxy_endpoint" {
  value = aws_db_proxy.this.endpoint
}

output "db_clients_security_group_id" {
  description = "Attach to anything needing DB access (ECS services, Lambdas, bootstrap task)."
  value       = aws_security_group.db_clients.id
}

output "master_secret_arn" {
  value = aws_secretsmanager_secret.master.arn
}

output "service_secret_arns" {
  value = { for k, s in aws_secretsmanager_secret.service : k => s.arn }
}
