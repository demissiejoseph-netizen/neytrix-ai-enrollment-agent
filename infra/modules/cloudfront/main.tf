# CloudFront module — STUB.
#
# Declares the interface consumed by infra/main.tf. Does NOT create resources
# yet. When implementing, serve the widget static bundle from the S3 bucket and
# route /api/* to the ALB origin, terminating TLS at the distribution.

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
}

variable "widget_bucket" {
  description = "S3 bucket name holding the built widget assets"
  type        = string
}

variable "api_domain" {
  description = "Origin domain for API requests (ALB DNS name)"
  type        = string
}

# TODO(ops): replace with aws_cloudfront_distribution / aws_s3_bucket /
# aws_cloudfront_origin_access_control.
locals {
  _placeholder_domain = "REPLACE_ME.cloudfront.net"
}

output "distribution_domain" {
  description = "Public domain of the CloudFront distribution"
  value       = local._placeholder_domain
}
