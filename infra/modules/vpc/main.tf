# VPC module — STUB.
#
# This declares the interface (inputs + outputs) that infra/main.tf consumes so
# the root configuration is internally consistent and `terraform validate` can
# reason about wiring. It does NOT yet create real AWS resources; the resource
# bodies must be authored as a deliberate infrastructure decision (subnet
# layout, AZ count, NAT strategy, cost). See infra/README.md.

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
}

variable "vpc_cidr" {
  description = "CIDR block for the VPC"
  type        = string
}

# TODO(ops): replace these placeholder locals with real aws_vpc / aws_subnet /
# aws_nat_gateway / aws_route_table resources before deploying.
locals {
  _placeholder_vpc_id      = "vpc-REPLACE_ME"
  _placeholder_private_ids = []
  _placeholder_public_ids  = []
}

output "vpc_id" {
  description = "ID of the VPC"
  value       = local._placeholder_vpc_id
}

output "private_subnet_ids" {
  description = "IDs of the private subnets (RDS, ECS tasks)"
  value       = local._placeholder_private_ids
}

output "public_subnet_ids" {
  description = "IDs of the public subnets (load balancer)"
  value       = local._placeholder_public_ids
}
