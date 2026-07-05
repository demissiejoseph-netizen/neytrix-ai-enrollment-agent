# Infrastructure Variables

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
  default     = "dev"
}

variable "aws_region" {
  description = "AWS region for resources"
  type        = string
  default     = "us-west-2"
}

# Networking
variable "vpc_cidr" {
  description = "CIDR block for VPC"
  type        = string
  default     = "10.0.0.0/16"
}

# Database
variable "db_name" {
  description = "PostgreSQL database name"
  type        = string
  default     = "neytrix_enrollment"
}

variable "db_username" {
  description = "PostgreSQL master username"
  type        = string
  default     = "postgres"
  sensitive   = true
}

variable "db_instance_class" {
  description = "RDS instance class"
  type        = string
  default     = "db.t4g.micro"
}

# Application
variable "app_image" {
  description = "Docker image for application"
  type        = string
  default     = "neytrix/enrollment-agent:latest"
}

variable "app_port" {
  description = "Application container port"
  type        = number
  default     = 8080
}

variable "ecs_desired_count" {
  description = "Desired number of ECS tasks"
  type        = number
  default     = 2
}

# Widget
variable "widget_s3_bucket" {
  description = "S3 bucket name for widget static files"
  type        = string
  default     = "neytrix-widget-assets"
}

# Tags
variable "tags" {
  description = "Additional tags for resources"
  type        = map(string)
  default     = {}
}
