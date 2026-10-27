output "ws_url" {
  description = "wss:// URL the SPA connects to."
  value       = "${aws_apigatewayv2_api.this.api_endpoint}/${aws_apigatewayv2_stage.live.name}"
}

output "callback_url" {
  description = "Management endpoint for PostToConnection (WS_CALLBACK_URL)."
  value       = local.callback_url
}

output "manage_connections_arn" {
  description = "Resource for execute-api:ManageConnections."
  value       = local.manage_arn
}
