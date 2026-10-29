output "api_endpoint" {
  description = "Public base URL; also the JWT issuer (G1)."
  value       = aws_apigatewayv2_api.this.api_endpoint
}

output "api_id" {
  value = aws_apigatewayv2_api.this.id
}
