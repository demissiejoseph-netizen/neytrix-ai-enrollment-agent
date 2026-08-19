-- ============================================================
-- Neytrix AI Enrollment Agent
-- Migration 001: Initial Schema
-- PostgreSQL 16 + pgvector + Row-Level Security
-- ============================================================

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ============================================================
-- TENANTS (organisations using the platform)
-- ============================================================
CREATE TABLE tenants (
  id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  slug          TEXT UNIQUE NOT NULL,
  name          TEXT NOT NULL,
  timezone      TEXT NOT NULL DEFAULT 'America/Los_Angeles',
  stripe_account_id TEXT,
  google_calendar_id TEXT,
  settings      JSONB NOT NULL DEFAULT '{}',
  is_active     BOOLEAN NOT NULL DEFAULT TRUE,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ============================================================
-- GUARDIANS
-- ============================================================
CREATE TABLE guardians (
  id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  first_name    TEXT NOT NULL,
  last_name     TEXT NOT NULL,
  email         TEXT NOT NULL,
  phone         TEXT,
  preferred_contact TEXT NOT NULL DEFAULT 'email' CHECK (preferred_contact IN ('email','phone')),
  gdpr_consented_at TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (tenant_id, email)
);

CREATE INDEX idx_guardians_tenant ON guardians(tenant_id);
CREATE INDEX idx_guardians_email ON guardians(tenant_id, email);

-- ============================================================
-- PLAYERS
-- ============================================================
CREATE TABLE players (
  id            UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id     UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  guardian_id   UUID NOT NULL REFERENCES guardians(id) ON DELETE CASCADE,
  first_name    TEXT NOT NULL,
  last_name     TEXT NOT NULL,
  date_of_birth DATE NOT NULL,
  gender        TEXT CHECK (gender IN ('male','female','non_binary','prefer_not_to_say')),
  medical_notes TEXT,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_players_tenant ON players(tenant_id);
CREATE INDEX idx_players_guardian ON players(guardian_id);

-- ============================================================
-- PROGRAMS
-- ============================================================
CREATE TABLE programs (
  id               UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id        UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  name             TEXT NOT NULL,
  description      TEXT,
  sport            TEXT NOT NULL,
  min_age_years    INT NOT NULL,
  max_age_years    INT NOT NULL,
  gender_policy    TEXT NOT NULL DEFAULT 'all' CHECK (gender_policy IN ('male','female','all')),
  skill_level      TEXT NOT NULL DEFAULT 'all' CHECK (skill_level IN ('beginner','intermediate','advanced','all')),
  capacity         INT NOT NULL,
  price_cents      BIGINT NOT NULL,
  deposit_cents    BIGINT NOT NULL DEFAULT 0,
  currency         TEXT NOT NULL DEFAULT 'usd',
  start_date       DATE NOT NULL,
  end_date         DATE NOT NULL,
  registration_open_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  registration_close_at TIMESTAMPTZ,
  location         TEXT,
  stripe_price_id  TEXT,
  is_active        BOOLEAN NOT NULL DEFAULT TRUE,
  created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_programs_tenant ON programs(tenant_id);
CREATE INDEX idx_programs_active ON programs(tenant_id, is_active);

-- ============================================================
-- REGISTRATIONS
-- ============================================================
CREATE TYPE registration_status AS ENUM (
  'inquiry', 'intake_complete', 'assessment_scheduled',
  'assessment_complete', 'waiver_sent', 'waiver_signed',
  'payment_pending', 'payment_complete', 'enrolled',
  'waitlisted', 'cancelled'
);

CREATE TABLE registrations (
  id              UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id       UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  guardian_id     UUID NOT NULL REFERENCES guardians(id),
  player_id       UUID NOT NULL REFERENCES players(id),
  program_id      UUID NOT NULL REFERENCES programs(id),
  status          registration_status NOT NULL DEFAULT 'inquiry',
  waitlist_position INT,
  stripe_payment_intent_id TEXT,
  stripe_checkout_session_id TEXT,
  amount_paid_cents BIGINT NOT NULL DEFAULT 0,
  waiver_sent_at  TIMESTAMPTZ,
  waiver_signed_at TIMESTAMPTZ,
  enrolled_at     TIMESTAMPTZ,
  notes           TEXT,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_registrations_tenant ON registrations(tenant_id);
CREATE INDEX idx_registrations_player ON registrations(player_id);
CREATE INDEX idx_registrations_program ON registrations(program_id);
CREATE INDEX idx_registrations_status ON registrations(tenant_id, status);

-- ============================================================
-- ASSESSMENTS / TRIALS
-- ============================================================
CREATE TABLE assessments (
  id               UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id        UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  registration_id  UUID NOT NULL REFERENCES registrations(id) ON DELETE CASCADE,
  google_event_id  TEXT,
  scheduled_at     TIMESTAMPTZ NOT NULL,
  duration_minutes INT NOT NULL DEFAULT 60,
  location         TEXT,
  notes            TEXT,
  outcome          TEXT CHECK (outcome IN ('pass','fail','pending','no_show')),
  assessed_at      TIMESTAMPTZ,
  created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_assessments_tenant ON assessments(tenant_id);
CREATE INDEX idx_assessments_registration ON assessments(registration_id);

-- ============================================================
-- CONVERSATION SESSIONS
-- ============================================================
CREATE TABLE conversation_sessions (
  id           UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  guardian_id  UUID REFERENCES guardians(id),
  session_token TEXT UNIQUE NOT NULL,
  channel      TEXT NOT NULL DEFAULT 'widget' CHECK (channel IN ('widget','email')),
  state        TEXT NOT NULL DEFAULT 'greeting',
  context      JSONB NOT NULL DEFAULT '{}',
  ended_at     TIMESTAMPTZ,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_sessions_tenant ON conversation_sessions(tenant_id);
CREATE INDEX idx_sessions_token ON conversation_sessions(session_token);

-- ============================================================
-- CONVERSATION MESSAGES
-- ============================================================
CREATE TABLE conversation_messages (
  id           UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  session_id   UUID NOT NULL REFERENCES conversation_sessions(id) ON DELETE CASCADE,
  tenant_id    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  role         TEXT NOT NULL CHECK (role IN ('user','assistant','tool')),
  content      TEXT NOT NULL,
  tool_name    TEXT,
  tool_args    JSONB,
  tool_result  JSONB,
  tokens_used  INT,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_messages_session ON conversation_messages(session_id);
CREATE INDEX idx_messages_tenant ON conversation_messages(tenant_id);

-- ============================================================
-- KNOWLEDGE BASE (RAG with pgvector)
-- ============================================================
CREATE TABLE knowledge_chunks (
  id           UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  source_type  TEXT NOT NULL CHECK (source_type IN ('faq','policy','program','custom')),
  source_ref   TEXT,
  content      TEXT NOT NULL,
  embedding    vector(1536),
  metadata     JSONB NOT NULL DEFAULT '{}',
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_knowledge_tenant ON knowledge_chunks(tenant_id);
CREATE INDEX idx_knowledge_embedding ON knowledge_chunks
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

-- ============================================================
-- AUDIT LOG
-- ============================================================
CREATE TABLE audit_log (
  id           UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id    UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  actor_type   TEXT NOT NULL CHECK (actor_type IN ('agent','staff','system')),
  actor_id     TEXT,
  action       TEXT NOT NULL,
  resource_type TEXT NOT NULL,
  resource_id  UUID,
  payload      JSONB,
  ip_address   INET,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_audit_tenant ON audit_log(tenant_id);
CREATE INDEX idx_audit_resource ON audit_log(resource_type, resource_id);

-- ============================================================
-- ROW-LEVEL SECURITY
-- ============================================================
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE guardians ENABLE ROW LEVEL SECURITY;
ALTER TABLE players ENABLE ROW LEVEL SECURITY;
ALTER TABLE programs ENABLE ROW LEVEL SECURITY;
ALTER TABLE registrations ENABLE ROW LEVEL SECURITY;
ALTER TABLE assessments ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE conversation_messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE knowledge_chunks ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;

-- App role uses current_setting to identify the tenant. The `true` (missing_ok) argument
-- returns NULL instead of raising "unrecognized configuration parameter" when a connection
-- has not yet called set_config('app.tenant_id', ...) -- NULL fails the equality predicate
-- and cleanly denies the row, rather than throwing a 500 on the first query of a fresh session.
CREATE POLICY tenant_isolation_guardians ON guardians
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_players ON players
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_programs ON programs
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_registrations ON registrations
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_assessments ON assessments
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_sessions ON conversation_sessions
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_messages ON conversation_messages
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_knowledge ON knowledge_chunks
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

CREATE POLICY tenant_isolation_audit ON audit_log
  USING (tenant_id = current_setting('app.tenant_id', true)::UUID);

-- `tenants` itself has no tenant_id column -- it IS the directory of tenants, so tenant
-- resolution (GetBySlugAsync) must run before any tenant is known. It holds no guardian/player
-- PII (slug, name, timezone, Stripe/Calendar ids, settings) so open SELECT is the correct
-- posture; mutation is deliberately left ungranted below so provisioning stays an out-of-band
-- operator action, not something reachable from a request-scoped connection.
CREATE POLICY tenant_isolation_tenants_select ON tenants
  FOR SELECT USING (true);

-- ============================================================
-- APPLICATION ROLE
-- ============================================================
-- Least-privilege role the API connects as (see .env.example, which already documented this
-- role name before it existed). FORCE ROW LEVEL SECURITY is defense in depth: without it, RLS
-- is skipped entirely for any role that happens to own these tables, silently disabling every
-- policy above. neytrix_app is never the owner, but forcing keeps that true even if ownership
-- changes later (e.g. a future migration run as neytrix_app itself).
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'neytrix_app') THEN
    CREATE ROLE neytrix_app LOGIN;
  END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO neytrix_app;
GRANT SELECT ON tenants TO neytrix_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON
  guardians, players, programs, registrations, assessments,
  conversation_sessions, conversation_messages, knowledge_chunks, audit_log
  TO neytrix_app;

ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
ALTER TABLE guardians FORCE ROW LEVEL SECURITY;
ALTER TABLE players FORCE ROW LEVEL SECURITY;
ALTER TABLE programs FORCE ROW LEVEL SECURITY;
ALTER TABLE registrations FORCE ROW LEVEL SECURITY;
ALTER TABLE assessments FORCE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;
ALTER TABLE conversation_messages FORCE ROW LEVEL SECURITY;
ALTER TABLE knowledge_chunks FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log FORCE ROW LEVEL SECURITY;

-- ============================================================
-- UPDATED_AT TRIGGER
-- ============================================================
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$;

CREATE TRIGGER trg_tenants_updated BEFORE UPDATE ON tenants FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_guardians_updated BEFORE UPDATE ON guardians FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_players_updated BEFORE UPDATE ON players FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_programs_updated BEFORE UPDATE ON programs FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_registrations_updated BEFORE UPDATE ON registrations FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_assessments_updated BEFORE UPDATE ON assessments FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_sessions_updated BEFORE UPDATE ON conversation_sessions FOR EACH ROW EXECUTE FUNCTION set_updated_at();
CREATE TRIGGER trg_knowledge_updated BEFORE UPDATE ON knowledge_chunks FOR EACH ROW EXECUTE FUNCTION set_updated_at();
