# Public edge (ADR-001): API Gateway HTTP API → VPC Link → internal ALB.
# JWTs are validated here before traffic enters the VPC. Only the routes that issue tokens or publish the
# signing keys are public (TDD API design); the catch-all requires a valid JWT.
#
# G1: the authorizer must fetch the issuer's discovery document over public HTTPS, so the token issuer IS this
# API's URL and /.well-known/* is routed (publicly) to the auth service.

locals {
  name = "${var.project}-${var.env}"
}

resource "aws_apigatewayv2_api" "this" {
  name          = "${local.name}-http"
  protocol_type = "HTTP"

  cors_configuration {
    allow_origins  = var.cors_origins
    allow_methods  = ["GET", "POST", "DELETE", "OPTIONS"]
    allow_headers  = ["authorization", "content-type", "idempotency-key"]
    expose_headers = ["etag"]
    max_age        = 3600
  }
}

resource "aws_security_group" "vpc_link" {
  name        = "${local.name}-vpc-link"
  description = "API Gateway VPC Link ENIs"
  vpc_id      = var.vpc_id

  egress {
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = [var.vpc_cidr]
  }
}

resource "aws_apigatewayv2_vpc_link" "this" {
  name               = local.name
  subnet_ids         = var.private_subnet_ids
  security_group_ids = [aws_security_group.vpc_link.id]
}

resource "aws_apigatewayv2_integration" "alb" {
  api_id             = aws_apigatewayv2_api.this.id
  integration_type   = "HTTP_PROXY"
  integration_method = "ANY"
  integration_uri    = var.alb_listener_arn
  connection_type    = "VPC_LINK"
  connection_id      = aws_apigatewayv2_vpc_link.this.id
  # 30 s is the HTTP API maximum; file bytes never come through here (ADR-002).
  timeout_milliseconds = 30000
}

# API Gateway fetches {issuer}/.well-known/openid-configuration when the authorizer is created. That document
# is served by the auth service through this very API, so the authorizer waits for the public routes, the stage,
# and a healthy auth service (var.issuer_ready carries that dependency without a module-level cycle).
resource "terraform_data" "issuer_ready" {
  input = var.issuer_ready
}

resource "aws_apigatewayv2_authorizer" "jwt" {
  api_id           = aws_apigatewayv2_api.this.id
  name             = "docbridge-jwt"
  authorizer_type  = "JWT"
  identity_sources = ["$request.header.Authorization"]

  jwt_configuration {
    issuer   = aws_apigatewayv2_api.this.api_endpoint
    audience = [var.jwt_audience]
  }

  depends_on = [terraform_data.issuer_ready, aws_apigatewayv2_route.public, aws_apigatewayv2_stage.default]
}

# Everything not listed below needs a valid JWT.
resource "aws_apigatewayv2_route" "default" {
  api_id             = aws_apigatewayv2_api.this.id
  route_key          = "$default"
  target             = "integrations/${aws_apigatewayv2_integration.alb.id}"
  authorization_type = "JWT"
  authorizer_id      = aws_apigatewayv2_authorizer.jwt.id
}

resource "aws_apigatewayv2_route" "public" {
  for_each  = toset(["POST /auth/register", "POST /auth/login", "POST /auth/token", "GET /.well-known/{proxy+}"])
  api_id    = aws_apigatewayv2_api.this.id
  route_key = each.value
  target    = "integrations/${aws_apigatewayv2_integration.alb.id}"
}

resource "aws_cloudwatch_log_group" "access" {
  name              = "/apigw/${local.name}-http"
  retention_in_days = var.log_retention_days
}

resource "aws_apigatewayv2_stage" "default" {
  api_id      = aws_apigatewayv2_api.this.id
  name        = "$default"
  auto_deploy = true

  default_route_settings {
    throttling_burst_limit = var.throttle_burst
    throttling_rate_limit  = var.throttle_rate
  }

  access_log_settings {
    destination_arn = aws_cloudwatch_log_group.access.arn
    format = jsonencode({
      requestId            = "$context.requestId", ip = "$context.identity.sourceIp", method = "$context.httpMethod",
      path                 = "$context.path", status = "$context.status", latencyMs = "$context.responseLatency",
      integrationLatencyMs = "$context.integrationLatency", authError = "$context.authorizer.error",
    })
  }
}
