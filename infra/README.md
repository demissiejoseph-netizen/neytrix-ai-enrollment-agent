# Infrastructure

Terraform configuration for deploying Neytrix AI Enrollment Agent on AWS.

## Architecture

- **Compute**: AWS ECS Fargate for containerized .NET API
- **Database**: RDS PostgreSQL with Row-Level Security (RLS)
- **CDN**: CloudFront for widget static assets
- **Networking**: VPC with public/private subnets across multiple AZs
- **Security**: AWS Secrets Manager for sensitive credentials

## Prerequisites

- Terraform >= 1.5
- AWS CLI configured with appropriate credentials
- Docker image built and pushed to ECR

## Quick Start

1. **Initialize Terraform**
   ```bash
   cd infra
   terraform init
   ```

2. **Configure Backend**
   Edit `main.tf` and update the S3 backend configuration.

3. **Set Variables**
   Create `terraform.tfvars`:
   ```hcl
   environment       = "dev"
   aws_region        = "us-west-2"
   db_username       = "your_db_user"
   app_image         = "<account-id>.dkr.ecr.us-west-2.amazonaws.com/enrollment-agent:latest"
   widget_s3_bucket  = "your-widget-bucket"
   ```

4. **Plan and Apply**
   ```bash
   terraform plan
   terraform apply
   ```

## Modules

The infrastructure is organized into modules:

- **vpc**: VPC, subnets, NAT gateways, route tables
- **database**: RDS PostgreSQL instance, security groups
- **ecs**: ECS cluster, task definition, ALB, auto-scaling
- **cloudfront**: CloudFront distribution for widget
- **secrets**: AWS Secrets Manager for API keys and credentials

## Environment Configuration

### Development
- Single-AZ deployment
- db.t4g.micro instance
- 2 ECS tasks

### Production
- Multi-AZ deployment
- db.r6g.large instance
- 4+ ECS tasks
- Enhanced monitoring

## Security

- All secrets stored in AWS Secrets Manager
- Database in private subnets only
- RLS policies enforce tenant isolation
- HTTPS/TLS encryption enforced

## Outputs

- `api_endpoint`: ALB DNS name for API access
- `cloudfront_domain`: CloudFront domain for widget
- `database_endpoint`: RDS endpoint (sensitive)

## Estimated Costs

**Dev Environment**: ~$50-70/month
- ECS Fargate: ~$30
- RDS db.t4g.micro: ~$15
- Data transfer & misc: ~$10

**Production**: ~$300-500/month (varies by traffic)

## Maintenance

- Database backups: Automated daily snapshots (7-day retention)
- Monitoring: CloudWatch logs and metrics
- Updates: Use `terraform plan` before applying changes
