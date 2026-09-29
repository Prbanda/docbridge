variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "vpc_cidr" {
  type    = string
  default = "10.0.0.0/16"
}

variable "az_count" {
  description = "Number of AZs. 2 meets the 99.9% availability target (TDD failure modes)."
  type        = number
  default     = 2
}
