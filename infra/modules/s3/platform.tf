# G3: the document platform's own store, where delivered documents live permanently (no lifecycle expiry).
# Same protections as staging: SSE-KMS with Bucket Keys, all public access blocked, TLS-only.

resource "aws_s3_bucket" "platform" {
  bucket        = "${local.name}-platform-docs-${data.aws_caller_identity.current.account_id}"
  force_destroy = var.force_destroy
}

resource "aws_s3_bucket_server_side_encryption_configuration" "platform" {
  bucket = aws_s3_bucket.platform.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "aws:kms"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_public_access_block" "platform" {
  bucket                  = aws_s3_bucket.platform.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_policy" "platform_tls_only" {
  bucket = aws_s3_bucket.platform.id

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Sid       = "DenyInsecureTransport"
      Effect    = "Deny"
      Principal = "*"
      Action    = "s3:*"
      Resource  = [aws_s3_bucket.platform.arn, "${aws_s3_bucket.platform.arn}/*"]
      Condition = { Bool = { "aws:SecureTransport" = "false" } }
    }]
  })

  depends_on = [aws_s3_bucket_public_access_block.platform]
}

output "platform_bucket_name" {
  value = aws_s3_bucket.platform.bucket
}

output "platform_bucket_arn" {
  value = aws_s3_bucket.platform.arn
}
