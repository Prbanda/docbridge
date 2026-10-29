variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "service" {
  description = "Service name (also the container name and the ECS service name)."
  type        = string
}

variable "image" {
  description = "Full image reference. Deploys push the git SHA and move the env tag, then force a new deployment."
  type        = string
}

variable "port" {
  type = number
}

variable "cpu" {
  type    = number
  default = 256
}

variable "memory" {
  type    = number
  default = 512
}

variable "ephemeral_storage_gib" {
  type    = number
  default = 21
}

variable "desired_count" {
  type    = number
  default = 2
}

variable "cluster_arn" {
  type = string
}

variable "vpc_id" {
  type = string
}

variable "private_subnet_ids" {
  type = list(string)
}

variable "alb_security_group_id" {
  type = string
}

variable "extra_security_group_ids" {
  description = "e.g. the db_clients SG for database access."
  type        = list(string)
  default     = []
}

variable "listener_arn" {
  type = string
}

variable "priority" {
  type = number
}

variable "path_patterns" {
  description = "ALB path patterns routed to this service (max 5 per rule)."
  type        = list(string)
}

variable "environment" {
  type    = map(string)
  default = {}
}

variable "secrets" {
  description = "Env var name → Secrets Manager valueFrom (ARN, or ARN:json-key:: for JSON secrets)."
  type        = map(string)
  default     = {}
}

variable "task_policy_json" {
  type    = string
  default = null
}

variable "log_retention_days" {
  type    = number
  default = 14
}

variable "wait_for" {
  description = "Any value the service must wait for before starting (e.g. the DB bootstrap result)."
  type        = string
  default     = ""
}
