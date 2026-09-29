data "aws_caller_identity" "current" {}

locals {
  name           = "${var.project}-${var.env}"
  staging_bucket = "${local.name}-staging-${data.aws_caller_identity.current.account_id}"
  spa_bucket     = "${local.name}-spa-${data.aws_caller_identity.current.account_id}"
}

# --- Staging bucket (ADR-002/003/004) ---------------------------------------

resource "aws_s3_bucket" "staging" {
  bucket        = local.staging_bucket
  force_destroy = var.force_destroy
}

# SSE-KMS with the AWS-managed aws/s3 key + Bucket Keys (ADR-003).
resource "aws_s3_bucket_server_side_encryption_configuration" "staging" {
  bucket = aws_s3_bucket.staging.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "aws:kms"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_public_access_block" "staging" {
  bucket                  = aws_s3_bucket.staging.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# 30-day retention + multipart-abort cleanup (TDD step 0.2).
resource "aws_s3_bucket_lifecycle_configuration" "staging" {
  bucket = aws_s3_bucket.staging.id

  rule {
    id     = "expire-staged-files"
    status = "Enabled"

    filter {}

    expiration {
      days = var.staging_retention_days
    }

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }
}

# TLS-only access (TDD Security & access).
resource "aws_s3_bucket_policy" "staging_tls_only" {
  bucket = aws_s3_bucket.staging.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "DenyInsecureTransport"
      Effect    = "Deny"
      Principal = "*"
      Action    = "s3:*"
      Resource = [
        aws_s3_bucket.staging.arn,
        "${aws_s3_bucket.staging.arn}/*",
      ]
      Condition = { Bool = { "aws:SecureTransport" = "false" } }
    }]
  })

  depends_on = [aws_s3_bucket_public_access_block.staging]
}

# Browser talks to the bucket directly via presigned URLs (ADR-002).
resource "aws_s3_bucket_cors_configuration" "staging" {
  bucket = aws_s3_bucket.staging.id

  cors_rule {
    allowed_methods = ["GET", "POST", "PUT"]
    allowed_origins = var.upload_cors_origins
    allowed_headers = ["*"]
    expose_headers  = ["ETag"]
    max_age_seconds = 3600
  }
}

# --- SPA hosting: private bucket + CloudFront OAC ---------------------------

resource "aws_s3_bucket" "spa" {
  bucket        = local.spa_bucket
  force_destroy = var.force_destroy
}

resource "aws_s3_bucket_public_access_block" "spa" {
  bucket                  = aws_s3_bucket.spa.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_cloudfront_origin_access_control" "spa" {
  name                              = "${local.name}-spa"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

resource "aws_cloudfront_distribution" "spa" {
  enabled             = true
  comment             = "${local.name} SPA"
  default_root_object = "index.html"
  price_class         = var.spa_price_class

  origin {
    domain_name              = aws_s3_bucket.spa.bucket_regional_domain_name
    origin_id                = "spa-s3"
    origin_access_control_id = aws_cloudfront_origin_access_control.spa.id
  }

  default_cache_behavior {
    target_origin_id       = "spa-s3"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    compress               = true

    # AWS managed CachingOptimized policy.
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"
  }

  # SPA routing: deep links resolve to index.html.
  custom_error_response {
    error_code         = 403
    response_code      = 200
    response_page_path = "/index.html"
  }

  custom_error_response {
    error_code         = 404
    response_code      = 200
    response_page_path = "/index.html"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}

resource "aws_s3_bucket_policy" "spa" {
  bucket = aws_s3_bucket.spa.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "AllowCloudFrontOAC"
      Effect    = "Allow"
      Principal = { Service = "cloudfront.amazonaws.com" }
      Action    = "s3:GetObject"
      Resource  = "${aws_s3_bucket.spa.arn}/*"
      Condition = {
        StringEquals = { "AWS:SourceArn" = aws_cloudfront_distribution.spa.arn }
      }
    }]
  })

  depends_on = [aws_s3_bucket_public_access_block.spa]
}
