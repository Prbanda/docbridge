variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "recovery_window_days" {
  description = "0 in dev so teardown/recreate never collides on a pending-deletion name."
  type        = number
  default     = 0
}
