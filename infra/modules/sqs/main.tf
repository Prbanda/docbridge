# One work queue + its dead-letter queue (ADR-005/008). maxReceiveCount 3 = "at least 3 attempts";
# the DLQ catches exhausted messages so nothing is silently lost.

resource "aws_sqs_queue" "dlq" {
  name                      = "${var.project}-${var.env}-${var.name}-dlq"
  message_retention_seconds = 1209600 # 14 days
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "this" {
  name                       = "${var.project}-${var.env}-${var.name}"
  visibility_timeout_seconds = var.visibility_timeout_seconds
  message_retention_seconds  = 1209600 # 14 days: survives a long database outage (TDD failure modes)
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true

  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.dlq.arn
    maxReceiveCount     = var.max_receive_count
  })
}

resource "aws_sqs_queue_redrive_allow_policy" "dlq" {
  queue_url = aws_sqs_queue.dlq.id
  redrive_allow_policy = jsonencode({
    redrivePermission = "byQueue"
    sourceQueueArns   = [aws_sqs_queue.this.arn]
  })
}

variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "name" {
  type = string
}

variable "visibility_timeout_seconds" {
  type = number
}

variable "max_receive_count" {
  type    = number
  default = 3
}

output "queue_url" {
  value = aws_sqs_queue.this.url
}

output "queue_arn" {
  value = aws_sqs_queue.this.arn
}

output "dlq_url" {
  value = aws_sqs_queue.dlq.url
}

output "dlq_arn" {
  value = aws_sqs_queue.dlq.arn
}

output "dlq_name" {
  value = aws_sqs_queue.dlq.name
}
