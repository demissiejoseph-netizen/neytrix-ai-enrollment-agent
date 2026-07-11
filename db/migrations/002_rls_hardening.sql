-- ============================================================
-- Neytrix AI Enrollment Agent
-- Migration 002: Row-Level Security hardening
--
-- Fixes two gaps in 001 that could silently defeat tenant isolation:
--
--   1. `tenants` had RLS ENABLED but NO policy. With RLS on and no policy the
--      table is default-deny, so the bootstrap slug->tenant lookup (which runs
--      before a tenant context exists) returned zero rows. This adds a policy
--      that permits the bootstrap lookup while still restricting an established
--      tenant session to its own row.
--
--   2. No table had FORCE ROW LEVEL SECURITY. RLS policies do NOT apply to a
--      table's OWNER by default, so if the application connects as the role that
--      owns these tables, every tenant_isolation_* policy is bypassed and the
--      whole isolation model is void. FORCE makes the owner subject to RLS too.
--
-- IMPORTANT (requires you / ops): FORCE RLS still does NOT apply to SUPERUSER or
-- BYPASSRLS roles. The application MUST connect as a dedicated, non-superuser
-- role WITHOUT the BYPASSRLS attribute. A template for that role is at the bottom
-- of this file (commented out because the concrete role name/password is a
-- deployment decision). Verify in production with:
--     SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;
-- both columns must be false.
-- ============================================================

-- ------------------------------------------------------------
-- 1. tenants: allow the pre-context bootstrap lookup, otherwise self-only.
--    The connection factory sets app.tenant_id to the all-zero GUID for the
--    bootstrap connections (slug resolution, tenant onboarding). current_setting
--    is read with missing_ok = true so an unset GUC yields NULL instead of error.
-- ------------------------------------------------------------
CREATE POLICY tenant_isolation_tenants ON tenants
  USING (
    current_setting('app.tenant_id', true) IS NULL
    OR current_setting('app.tenant_id', true) = ''
    OR current_setting('app.tenant_id', true) = '00000000-0000-0000-0000-000000000000'
    OR id = current_setting('app.tenant_id', true)::uuid
  );

-- ------------------------------------------------------------
-- 2. FORCE RLS on every tenant-scoped table so the owner cannot bypass it.
-- ------------------------------------------------------------
ALTER TABLE tenants               FORCE ROW LEVEL SECURITY;
ALTER TABLE guardians             FORCE ROW LEVEL SECURITY;
ALTER TABLE players               FORCE ROW LEVEL SECURITY;
ALTER TABLE programs              FORCE ROW LEVEL SECURITY;
ALTER TABLE registrations         FORCE ROW LEVEL SECURITY;
ALTER TABLE assessments           FORCE ROW LEVEL SECURITY;
ALTER TABLE conversation_sessions FORCE ROW LEVEL SECURITY;
ALTER TABLE conversation_messages FORCE ROW LEVEL SECURITY;
ALTER TABLE knowledge_chunks      FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_log             FORCE ROW LEVEL SECURITY;

-- ------------------------------------------------------------
-- 3. Dedicated application role (TEMPLATE — uncomment and set a real password,
--    or provision via your secrets manager / IaC). The role must NOT be a
--    superuser and must NOT have BYPASSRLS, or FORCE RLS above is meaningless.
-- ------------------------------------------------------------
-- CREATE ROLE neytrix_app LOGIN PASSWORD 'set-via-secrets-manager' NOSUPERUSER NOBYPASSRLS;
-- GRANT USAGE ON SCHEMA public TO neytrix_app;
-- GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO neytrix_app;
-- GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO neytrix_app;
-- ALTER DEFAULT PRIVILEGES IN SCHEMA public
--   GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO neytrix_app;
