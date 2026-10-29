#!/bin/bash
# One-time Terraform state backend bootstrap (ADR-012, amended AM-8): a single
# versioned S3 bucket. State locking uses S3-native lockfiles (Terraform
# >= 1.10 `use_lockfile`), so no DynamoDB table is needed.
# The single manually created resource in the project.
#
# Usage: AWS_PROFILE=<profile> ./bootstrap/create-state-backend.sh [suffix]
# The suffix defaults to your 12-digit account id (bucket names are global).
set -euo pipefail

REGION="${AWS_REGION:-us-east-1}"
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
SUFFIX="${1:-$ACCOUNT_ID}"
BUCKET="docbridge-tfstate-${SUFFIX}"

echo "Creating state bucket s3://${BUCKET} in ${REGION}"
if [ "$REGION" = "us-east-1" ]; then
  aws s3api create-bucket --bucket "$BUCKET" --region "$REGION"
else
  aws s3api create-bucket --bucket "$BUCKET" --region "$REGION" \
    --create-bucket-configuration LocationConstraint="$REGION"
fi

aws s3api put-bucket-versioning --bucket "$BUCKET" \
  --versioning-configuration Status=Enabled

aws s3api put-bucket-encryption --bucket "$BUCKET" \
  --server-side-encryption-configuration '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"aws:kms"},"BucketKeyEnabled":true}]}'

aws s3api put-public-access-block --bucket "$BUCKET" \
  --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true

cat <<EOF

Done. Write these values into infra/envs/<env>/backend.hcl:

  bucket       = "${BUCKET}"
  key          = "docbridge/<env>/terraform.tfstate"
  region       = "${REGION}"
  use_lockfile = true
  encrypt      = true
EOF
