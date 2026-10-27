variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "vpc_id" {
  type = string
}

variable "vpc_cidr" {
  type = string
}

variable "private_subnet_ids" {
  type = list(string)
}

variable "db" {
  description = "Outputs of modules/rds."
  type = object({
    endpoint                  = string
    proxy_endpoint            = string
    clients_security_group_id = string
    master_secret_arn         = string
    service_secret_arns       = map(string)
  })
}

variable "staging_bucket" {
  type = object({ name = string, arn = string })
}

variable "platform_bucket" {
  type = object({ name = string, arn = string })
}

variable "spa_url" {
  description = "https:// origin of the SPA (CloudFront), allowed by CORS."
  type        = string
}

variable "extra_cors_origins" {
  type    = list(string)
  default = []
}

variable "image_tag" {
  description = "Mutable env tag the deploy moves to the new git SHA before forcing a new ECS deployment."
  type        = string
}

variable "lambda_package_dir" {
  type = string
}

variable "desired_count" {
  type    = number
  default = 2
}

variable "delivery_timeout_seconds" {
  type    = number
  default = 300
}

variable "delivery_reserved_concurrency" {
  description = "TDD 2.4 wants 50. Needs a Lambda concurrency quota above 60; -1 = unreserved (new accounts have 10)."
  type        = number
  default     = -1
}

variable "delivery_max_concurrency" {
  type    = number
  default = 50
}

variable "retry_backoff_seconds" {
  type    = string
  default = "60,300"
}

variable "secret_recovery_window_days" {
  type    = number
  default = 0
}
