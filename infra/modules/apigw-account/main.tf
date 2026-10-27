# Account-level API Gateway setting: the CloudWatch role REST and WebSocket APIs use to write logs.
# One per account and region, so it lives in envs/global, never in an environment.

resource "aws_iam_role" "cloudwatch" {
  name = "${var.project}-apigw-cloudwatch"
  assume_role_policy = jsonencode({
    Version   = "2012-10-17"
    Statement = [{ Effect = "Allow", Principal = { Service = "apigateway.amazonaws.com" }, Action = "sts:AssumeRole" }]
  })
}

resource "aws_iam_role_policy_attachment" "cloudwatch" {
  role       = aws_iam_role.cloudwatch.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonAPIGatewayPushToCloudWatchLogs"
}

resource "aws_api_gateway_account" "this" {
  cloudwatch_role_arn = aws_iam_role.cloudwatch.arn
  depends_on          = [aws_iam_role_policy_attachment.cloudwatch]
}

variable "project" {
  type    = string
  default = "docbridge"
}
