# Account-level resources shared by all environments: ECR repositories.
# Images are tagged by git SHA and promoted dev -> prod, so repos are global.

module "ecr" {
  source = "../../modules/ecr"
}
