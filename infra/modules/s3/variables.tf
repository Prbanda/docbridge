variable "project" {
  type    = string
  default = "docbridge"
}

variable "env" {
  type = string
}

variable "staging_retention_days" {
  description = "Staged files retained >= 30 days after job completion (assumption A2)."
  type        = number
  default     = 30
}

variable "spa_price_class" {
  type    = string
  default = "PriceClass_100"
}

variable "force_destroy" {
  description = "Allow terraform destroy to delete non-empty buckets. true in dev (cheap teardown), false in prod."
  type        = bool
  default     = false
}

variable "upload_cors_origins" {
  description = "Origins allowed to use presigned POST/GET against staging (the SPA URL). Tightened once CloudFront exists."
  type        = list(string)
  default     = ["*"]
}
