# RDS PostgreSQL module — STUB.
#
# Declares the interface consumed by infra/main.tf. Does NOT create resources
# yet. When implementing, the instance must run PostgreSQL 16 with the pgvector
# extension available, and the application must connect as a dedicated
# non-superuser / NOBYPASSRLS role (see db/migrations/002_rls_hardening.sql),
# NOT the master user, or tenant Row-Level Security is silently bypassed.

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
}

variable "vpc_id" {
  description = "VPC to place the database in"
  type        = string
}

variable "private_subnet_ids" {
  description = "Private subnets for the DB subnet group"
  type        = list(string)
}

variable "db_name" {
  description = "Initial database name"
  type        = string
}

variable "db_username" {
  description = "Master username"
  type        = string
  sensitive   = true
}

variable "db_instance_class" {
  description = "RDS instance class"
  type        = string
}

# TODO(ops): replace with aws_db_instance / aws_db_subnet_group /
# aws_security_group and a Secrets Manager-managed password.
locals {
  _placeholder_endpoint           = "REPLACE_ME.rds.amazonaws.com:5432"
  _placeholder_password_secret_arn = "arn:aws:secretsmanager:REGION:ACCOUNT:secret:REPLACE_ME"
}

output "db_endpoint" {
  description = "Database connection endpoint"
  value       = local._placeholder_endpoint
}

output "db_password_secret_arn" {
  description = "ARN of the Secrets Manager secret holding the DB password"
  value       = local._placeholder_password_secret_arn
}
