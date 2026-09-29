# ECR repositories are shared across environments (images are tagged by git
# SHA and promoted dev → prod), so this module takes no env variable.

resource "aws_ecr_repository" "this" {
  for_each     = toset(var.repositories)
  name         = "${var.project}/${each.key}"
  force_delete = true # allow clean destroy even with images present

  image_scanning_configuration {
    scan_on_push = true
  }
}

resource "aws_ecr_lifecycle_policy" "this" {
  for_each   = aws_ecr_repository.this
  repository = each.value.name

  policy = jsonencode({
    rules = [{
      rulePriority = 1
      description  = "keep last 20 images"
      selection = {
        tagStatus   = "any"
        countType   = "imageCountMoreThan"
        countNumber = 20
      }
      action = { type = "expire" }
    }]
  })
}
