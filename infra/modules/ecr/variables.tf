variable "project" {
  type    = string
  default = "docbridge"
}

variable "repositories" {
  type    = list(string)
  default = ["auth", "platform", "docbridge-api"]
}
