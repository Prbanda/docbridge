# Internal ALB (ADR-001): routes API traffic inside the VPC to one target group per service by path.
# Not reachable from the internet: private subnets, ingress only from inside the VPC (VPC Link ENIs, Lambdas).

locals {
  name = "${var.project}-${var.env}"
}

resource "aws_security_group" "alb" {
  name        = "${local.name}-alb"
  description = "Internal ALB"
  vpc_id      = var.vpc_id

  ingress {
    description = "HTTP from inside the VPC only"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = [var.vpc_cidr]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = [var.vpc_cidr]
  }
}

resource "aws_lb" "this" {
  name               = "${local.name}-internal"
  internal           = true
  load_balancer_type = "application"
  subnets            = var.private_subnet_ids
  security_groups    = [aws_security_group.alb.id]
  # 1 GB ingest streams from the delivery worker to the platform can take minutes.
  idle_timeout = 300
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.this.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "fixed-response"
    fixed_response {
      content_type = "application/json"
      status_code  = "404"
      message_body = "{\"error\":{\"code\":\"NOT_FOUND\",\"message\":\"no route\"}}"
    }
  }
}
