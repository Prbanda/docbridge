variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "function" {
  description = "Short function name, e.g. delivery."
  type        = string
}

variable "handler" {
  description = "Assembly::Namespace.Class::Method"
  type        = string
}

variable "package_path" {
  description = "Zip built by scripts/build-lambdas.sh. A placeholder is used if it doesn't exist."
  type        = string
}

variable "memory_mb" {
  type    = number
  default = 512
}

variable "timeout_seconds" {
  type    = number
  default = 30
}

variable "reserved_concurrency" {
  type    = number
  default = -1
}

variable "environment" {
  type    = map(string)
  default = {}
}

variable "subnet_ids" {
  type    = list(string)
  default = []
}

variable "security_group_ids" {
  type    = list(string)
  default = []
}

variable "secret_arns" {
  type    = list(string)
  default = []
}

variable "policy_json" {
  type    = string
  default = null
}

variable "sqs_trigger_arn" {
  type    = string
  default = null
}

variable "batch_size" {
  type    = number
  default = 1
}

variable "max_concurrency" {
  description = "Event source mapping maximum concurrency (2-1000)."
  type        = number
  default     = null
}

variable "schedule_expression" {
  type    = string
  default = null
}

variable "log_retention_days" {
  type    = number
  default = 14
}

variable "sqs_trigger_enabled" {
  description = "Set with sqs_trigger_arn (a known bool keeps count plannable before the queue exists)."
  type        = bool
  default     = false
}
