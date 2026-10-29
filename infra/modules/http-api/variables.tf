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

variable "alb_listener_arn" {
  type = string
}

variable "jwt_audience" {
  type    = string
  default = "docbridge"
}

variable "cors_origins" {
  type = list(string)
}

variable "throttle_burst" {
  type    = number
  default = 500
}

variable "throttle_rate" {
  type    = number
  default = 200
}

variable "log_retention_days" {
  type    = number
  default = 14
}

variable "issuer_ready" {
  description = "Any value that only exists once the auth service is serving (e.g. its ECS service name)."
  type        = string
  default     = ""
}
