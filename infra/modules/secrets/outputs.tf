output "jwt_private_key_arn" {
  value = aws_secretsmanager_secret.jwt.arn
}

output "client_secret_arns" {
  description = "Map: client id → secret ARN."
  value       = { for k, s in aws_secretsmanager_secret.client : k => s.arn }
}

output "all_arns" {
  value = concat([aws_secretsmanager_secret.jwt.arn], [for s in aws_secretsmanager_secret.client : s.arn])
}
