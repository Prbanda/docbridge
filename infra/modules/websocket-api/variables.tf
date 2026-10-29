variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "connect_function" {
  type = object({ function_name = string, invoke_arn = string })
}

variable "disconnect_function" {
  type = object({ function_name = string, invoke_arn = string })
}

variable "subscribe_function" {
  type = object({ function_name = string, invoke_arn = string })
}

variable "log_retention_days" {
  type    = number
  default = 14
}
