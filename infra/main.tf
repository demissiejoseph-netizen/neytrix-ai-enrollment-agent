# Neytrix AI Enrollment Agent - Infrastructure as Code
# AWS ECS Fargate + RDS PostgreSQL + CloudFront

terraform {
  required_version = ">= 1.5"
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
  backend "s3" {
    # Configure backend with:
    # bucket = "neytrix-terraform-state"
    # key    = "enrollment-agent/terraform.tfstate"
    # region = "us-west-2"
    # encrypt = true
  }
}

provider "aws" {
  region = var.aws_region
  default_tags {
    tags = {
      Project     = "Neytrix AI Enrollment Agent"
      Environment = var.environment
      ManagedBy   = "Terraform"
    }
  }
}

# VPC and Networking
module "vpc" {
  source = "./modules/vpc"
  
  environment = var.environment
  vpc_cidr    = var.vpc_cidr
}

# RDS PostgreSQL Database
module "database" {
  source = "./modules/database"
  
  environment         = var.environment
  vpc_id              = module.vpc.vpc_id
  private_subnet_ids  = module.vpc.private_subnet_ids
  db_name             = var.db_name
  db_username         = var.db_username
  db_instance_class   = var.db_instance_class
}

# ECS Fargate Cluster
module "ecs" {
  source = "./modules/ecs"
  
  environment         = var.environment
  vpc_id              = module.vpc.vpc_id
  private_subnet_ids  = module.vpc.private_subnet_ids
  public_subnet_ids   = module.vpc.public_subnet_ids
  
  app_image           = var.app_image
  app_port            = var.app_port
  desired_count       = var.ecs_desired_count
  
  db_host             = module.database.db_endpoint
  db_name             = var.db_name
  db_username         = var.db_username
  db_password_secret  = module.database.db_password_secret_arn
}

# CloudFront CDN for Widget
module "cloudfront" {
  source = "./modules/cloudfront"
  
  environment     = var.environment
  widget_bucket   = var.widget_s3_bucket
  api_domain      = module.ecs.alb_dns_name
}

# Security and Secrets
module "secrets" {
  source = "./modules/secrets"
  
  environment = var.environment
}

# Outputs
output "api_endpoint" {
  description = "API Load Balancer endpoint"
  value       = module.ecs.alb_dns_name
}

output "cloudfront_domain" {
  description = "CloudFront distribution domain for widget"
  value       = module.cloudfront.distribution_domain
}

output "database_endpoint" {
  description = "RDS PostgreSQL endpoint"
  value       = module.database.db_endpoint
  sensitive   = true
}
