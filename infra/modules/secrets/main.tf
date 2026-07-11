# Secrets module — STUB.
#
# Declares the interface consumed by infra/main.tf. Does NOT create resources
# yet. When implementing, provision Secrets Manager entries for the values in
# .env.example (Stripe secret + webhook secret, Google service-account key,
# DB password) so nothing sensitive is baked into images or task definitions.

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
}

# TODO(ops): replace with aws_secretsmanager_secret /
# aws_secretsmanager_secret_version resources (one per secret in .env.example).
# Intentionally exposes no outputs until the concrete secrets are defined.
