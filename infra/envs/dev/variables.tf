variable "region" {
  type    = string
  default = "us-east-1"
}

# Assumption A4: verify the newest RDS Proxy-supported major version at first
# apply and keep .env POSTGRES_VERSION in sync.
variable "db_engine_version" {
  type    = string
  default = "17"
}
