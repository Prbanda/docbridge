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

variable "private_subnet_ids" {
  type = list(string)
}

# Assumption A4: pin the newest major version RDS Proxy supports in the region.
# Verify at first apply; the local POSTGRES_VERSION in .env must match.
variable "engine_version" {
  type    = string
  default = "17"
}

variable "instance_class" {
  type    = string
  default = "db.t4g.micro"
}

variable "allocated_storage_gb" {
  type    = number
  default = 20
}

variable "multi_az" {
  type    = bool
  default = false
}

variable "deletion_protection" {
  type    = bool
  default = false
}
