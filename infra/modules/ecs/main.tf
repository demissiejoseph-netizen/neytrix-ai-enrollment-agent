# ECS Fargate module — STUB.
#
# Declares the interface consumed by infra/main.tf. Does NOT create resources
# yet. When implementing, wire the DB password from db_password_secret into the
# task definition via `secrets` (not `environment`), and configure the ALB
# health check to hit GET /health (see Dockerfile / API host).

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
}

variable "vpc_id" {
  description = "VPC id"
  type        = string
}

variable "private_subnet_ids" {
  description = "Private subnets for ECS tasks"
  type        = list(string)
}

variable "public_subnet_ids" {
  description = "Public subnets for the load balancer"
  type        = list(string)
}

variable "app_image" {
  description = "Container image for the API"
  type        = string
}

variable "app_port" {
  description = "Container port the API listens on"
  type        = number
}

variable "desired_count" {
  description = "Desired number of running tasks"
  type        = number
}

variable "db_host" {
  description = "Database endpoint"
  type        = string
}

variable "db_name" {
  description = "Database name"
  type        = string
}

variable "db_username" {
  description = "Database username"
  type        = string
  sensitive   = true
}

variable "db_password_secret" {
  description = "ARN of the Secrets Manager secret holding the DB password"
  type        = string
}

# TODO(ops): replace with aws_ecs_cluster / aws_ecs_service /
# aws_ecs_task_definition / aws_lb / aws_lb_target_group (health check /health).
locals {
  _placeholder_alb_dns = "REPLACE_ME.elb.amazonaws.com"
}

output "alb_dns_name" {
  description = "Public DNS name of the application load balancer"
  value       = local._placeholder_alb_dns
}
