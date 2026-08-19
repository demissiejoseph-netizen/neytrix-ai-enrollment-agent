# Neytrix AI Enrollment Agent

**B2B SaaS AI receptionist for youth sports organizations**

Built on .NET 8, PostgreSQL/pgvector, Next.js, Stripe, and Google Calendar.

## Overview

Neytrix AI Enrollment Agent automates end-to-end youth sports enrollment through conversational AI. Organizations embed a chat widget on their website, enabling guardians to:

- Ask program questions (FAQ)
- Register player information
- Receive program recommendations based on age, skill, and preferences
- Book assessment slots via Google Calendar
- Sign waivers and complete Stripe payments
- Receive registration confirmations

All with multi-tenant isolation, audit trails, and compliance safeguards.

## Key Features

- **Conversational AI**: Natural language workflows (A-G) for enrollment
- **Multi-Tenancy**: Row-Level Security (RLS) ensures zero cross-tenant data exposure
- **Payment Integration**: Stripe Checkout for secure payments
- **Calendar Booking**: Google Calendar API for real-time slot availability
- **Deterministic Rules**: Age, capacity, and skill-level eligibility checks
- **RAG Knowledge Base**: pgvector-powered FAQ answers
- **Escalation**: Human handoff for edge cases
- **Audit Trail**: Immutable conversation logs for compliance

## Architecture

### Backend (.NET 8)
- **Domain Layer**: Entities (Tenant, Guardian, Player, Program, Registration)
- **API Layer**: REST endpoints for chat sessions and webhooks
- **Infrastructure**: Stripe and Google Calendar adapters
- **Middleware**: Tenant resolution via `X-Tenant-Slug` header

### Database (PostgreSQL)
- **Extensions**: pgvector for embeddings, uuid-ossp
- **RLS Policies**: Enforce tenant isolation at row level
- **Triggers**: Maintain audit logs and updated timestamps

### Frontend (Next.js)
- **Widget**: Embeddable chat component with session management
- **Deployment**: CloudFront CDN for global distribution

### Infrastructure (AWS)
- **Compute**: ECS Fargate for containerized API
- **Database**: RDS PostgreSQL
- **CDN**: CloudFront for widget assets
- **Secrets**: AWS Secrets Manager

## Repository Structure

```
neytrix-ai-enrollment-agent/
├── src/
│   ├── NeytrixAI.Domain/         # Domain entities and business logic
│   ├── NeytrixAI.Api/            # REST API controllers
│   └── NeytrixAI.Infrastructure/ # Stripe, Google Calendar adapters
├── widget/
│   └── src/components/           # Next.js chat widget
├── db/migrations/                # PostgreSQL DDL
├── docs/                         # OpenAPI spec
├── infra/                        # Terraform IaC
└── README.md
```

## Quick Start

### Prerequisites
- .NET 8 SDK
- PostgreSQL 15+ (with pgvector extension)
- Node.js 18+
- Stripe and Google Calendar API credentials

### 1. Database Setup

```bash
psql -U postgres -f db/migrations/001_initial_schema.sql
```

### 2. Configure API

Create `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=neytrix_enrollment;Username=postgres;Password=your_password"
  },
  "Stripe": {
    "SecretKey": "sk_test_...",
    "WebhookSecret": "whsec_..."
  },
  "GoogleCalendar": {
    "ClientId": "your_client_id",
    "ClientSecret": "your_client_secret"
  }
}
```

### 3. Run API

```bash
cd src/NeytrixAI.Api
dotnet run
```

### 4. Run Widget

```bash
cd widget
npm install
npm run dev
```

### 5. Embed Widget

```html
<div id="neytrix-chat"></div>
<script src="https://cdn.neytrix.ai/widget.js"></script>
<script>
  NeytrixChat.init({
    tenantSlug: 'your-org-slug',
    apiUrl: 'https://api.neytrix.ai'
  });
</script>
```

## Pilot Success Metrics

### P0 Scope (Phase 1)

| Metric | Target | Measurement |
|--------|--------|-------------|
| FAQ Accuracy | 85% | Guardian satisfaction (thumbs up/down) |
| Program Match Quality | 90% | Successful conversions to booking |
| Slot Booking Validity | 95% | No double-bookings, correct timeslots |
| Cross-Tenant Isolation | 100% | Zero data leaks (automated tests) |
| End-to-End Enrollments | 10+ | Pilot completions within 4 weeks |

### Phase 2-6 Roadmap

- **Phase 2**: Multi-language support, SMS notifications
- **Phase 3**: Advanced analytics dashboard
- **Phase 4**: Mobile app integration
- **Phase 5**: AI-powered waitlist management
- **Phase 6**: Billing and revenue hardening

## Security & Compliance

- **Multi-Tenancy**: PostgreSQL RLS enforces tenant isolation
- **Data Privacy**: GDPR/COPPA considerations for youth data
- **Audit Logs**: Immutable conversation records
- **Encryption**: TLS in transit, AES-256 at rest
- **Secrets**: AWS Secrets Manager (no hardcoded credentials)

## API Documentation

See [docs/openapi.yaml](docs/openapi.yaml) for full API specification.

### Key Endpoints

- `POST /chat/sessions` - Start a new chat session
- `POST /chat/sessions/{id}/messages` - Send a message
- `POST /webhooks/stripe` - Stripe webhook handler
- `POST /webhooks/google-calendar` - Calendar event updates

## Deployment

See [infra/README.md](infra/README.md) for Terraform deployment guide.

**Estimated AWS Costs**:
- Dev: ~$50-70/month
- Production: ~$300-500/month (scales with traffic)

## Testing

```bash
# Run unit tests
dotnet test

# Run integration tests (requires DB)
cd src/NeytrixAI.Api.Tests
dotnet test --filter Category=Integration
```

## Contributing

This is a prototype scaffold. For production:
1. Implement module stubs (VPC, ECS, RDS, CloudFront)
2. Add comprehensive error handling
3. Expand test coverage (unit, integration, E2E)
4. Implement observability (logging, metrics, tracing)
5. Add rate limiting and DDoS protection

### Progress log

Every commit that changes application behavior (`src/`, `tests/`,
`db/migrations/`, or a `.csproj`/`.sln` file) must add a dated entry to
[PROGRESS.md](PROGRESS.md) in the same commit — see that file for the exact
format. This is enforced in CI
(`.github/workflows/require-progress-notes.yml`) and locally via a git hook.
Enable the local hook once per clone with:

```bash
git config core.hooksPath .githooks
```

## License

Proprietary - Neytrix AI Inc.

## Support

For issues or questions: support@neytrix.ai
