# API Gateway WebSocket API (ADR-007): $connect / subscribe / $disconnect → Lambda handlers.
# Services stay private; workers push with PostToConnection on the management endpoint.

data "aws_region" "current" {}
data "aws_caller_identity" "current" {}

locals {
  name   = "${var.project}-${var.env}"
  routes = { "$connect" = var.connect_function, "$disconnect" = var.disconnect_function, "subscribe" = var.subscribe_function }
}

resource "aws_apigatewayv2_api" "this" {
  name                       = "${local.name}-ws"
  protocol_type              = "WEBSOCKET"
  route_selection_expression = "$request.body.action"
}

resource "aws_apigatewayv2_integration" "route" {
  for_each           = local.routes
  api_id             = aws_apigatewayv2_api.this.id
  integration_type   = "AWS_PROXY"
  integration_method = "POST"
  integration_uri    = each.value.invoke_arn
}

# $connect auth is done by the connect Lambda (JWT in the query string, validated against the JWKS, TDD 3.2).
resource "aws_apigatewayv2_route" "route" {
  for_each  = local.routes
  api_id    = aws_apigatewayv2_api.this.id
  route_key = each.key
  target    = "integrations/${aws_apigatewayv2_integration.route[each.key].id}"
}

resource "aws_lambda_permission" "route" {
  for_each      = local.routes
  statement_id  = "AllowWebSocket-${replace(each.key, "$", "")}"
  action        = "lambda:InvokeFunction"
  function_name = each.value.function_name
  principal     = "apigateway.amazonaws.com"
  source_arn    = "${aws_apigatewayv2_api.this.execution_arn}/*/${each.key}"
}

resource "aws_cloudwatch_log_group" "access" {
  name              = "/apigw/${local.name}-ws"
  retention_in_days = var.log_retention_days
}

resource "aws_apigatewayv2_stage" "live" {
  api_id      = aws_apigatewayv2_api.this.id
  name        = "live"
  auto_deploy = true

  default_route_settings {
    throttling_burst_limit = 200
    throttling_rate_limit  = 100
    data_trace_enabled     = false
  }

  # AM-7: the JWT rides in the connect query string, so the access-log format carries no query string or
  # request URL fields, only ids and outcomes.
  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.access.arn
    format = jsonencode({
      requestId = "$context.requestId", connectionId = "$context.connectionId", routeKey = "$context.routeKey",
      eventType = "$context.eventType", status = "$context.status", integrationError = "$context.integrationErrorMessage",
    })
  }
}

locals {
  callback_url = "https://${aws_apigatewayv2_api.this.id}.execute-api.${data.aws_region.current.region}.amazonaws.com/${aws_apigatewayv2_stage.live.name}"
  manage_arn   = "arn:aws:execute-api:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:${aws_apigatewayv2_api.this.id}/${aws_apigatewayv2_stage.live.name}/*/@connections/*"
}
