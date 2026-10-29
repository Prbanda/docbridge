output "listener_arn" {
  value = aws_lb_listener.http.arn
}

output "security_group_id" {
  value = aws_security_group.alb.id
}

output "dns_name" {
  value = aws_lb.this.dns_name
}

output "internal_url" {
  value = "http://${aws_lb.this.dns_name}"
}
