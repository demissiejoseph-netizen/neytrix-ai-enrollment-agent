-- ============================================================
-- Neytrix AI Enrollment Agent
-- Migration 003: Clerk identity integration
--
-- Links Clerk-authenticated guardians to their `guardians` row.
--
-- This migration is ADDITIVE and OPTIONAL. The column is nullable, so:
--   * anonymous (unauthenticated) widget conversations keep working exactly as
--     before — a guardian created through the normal INTAKE flow simply leaves
--     clerk_user_id NULL, and
--   * guardian rows created via staff/manual entry (which never originate from a
--     Clerk signup) are equally valid with a NULL clerk_user_id.
--
-- Nothing about tenant Row-Level Security (001/002), consent gating, payment,
-- eligibility, or the conversation state machine is changed here.
-- ============================================================

-- 1. Clerk user id on guardians. Nullable on purpose (see header).
ALTER TABLE guardians
  ADD COLUMN IF NOT EXISTS clerk_user_id TEXT;

-- 2. UNIQUE index on clerk_user_id.
--    A partial (WHERE clerk_user_id IS NOT NULL) UNIQUE index enforces that a
--    given Clerk user maps to at most one guardian row, while still allowing
--    many rows to have a NULL clerk_user_id (the anonymous / manual-entry case).
--    This doubles as the lookup index for "resolve guardian by clerk_user_id".
CREATE UNIQUE INDEX IF NOT EXISTS idx_guardians_clerk_user_id
  ON guardians (clerk_user_id)
  WHERE clerk_user_id IS NOT NULL;

-- Note: guardians already has RLS ENABLED + FORCE + tenant_isolation_guardians
-- (migrations 001/002). Adding a column does not alter those policies, so tenant
-- isolation continues to apply to reads/writes that touch clerk_user_id.
