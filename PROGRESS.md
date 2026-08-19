# Progress Log

This file is the running, human-readable history of meaningful changes to the
Neytrix AI Enrollment Agent codebase. It exists so that anyone (including a
future session of an AI assistant) can reconstruct *why* the code looks the
way it does without re-deriving it from the diff alone.

## Convention

- **Every commit that changes application behavior** — anything under
  `src/`, `tests/`, `db/migrations/`, or a `.csproj`/`.sln` file — must add a
  dated entry to this file in the **same commit**. Pure formatting, comment,
  or `.gitignore`-style housekeeping commits are exempt, but when in doubt,
  add an entry.
- **New entries go at the top**, directly under this header, newest first.
- **Entry format:**

  ```
  ## YYYY-MM-DD — Short title

  - What changed and why (not just what — the reasoning).
  - Bugs found/fixed, with the symptom and root cause if it's a bug fix.
  - Anything explicitly *not* done, so the next person doesn't assume it was.
  - Verification: what was run and what the result was (tests, build).
  ```

- This convention is enforced automatically:
  - **CI**: `.github/workflows/require-progress-notes.yml` fails the build if
    a pull request or push touches code paths without also touching this
    file.
  - **Local**: `.githooks/pre-commit` runs the same check before a commit is
    created. Enable it once per clone with:
    ```
    git config core.hooksPath .githooks
    ```

---

## 2026-08-19 — Add this progress log and its enforcement

- Added this file and the convention described above.
- Added `.github/workflows/require-progress-notes.yml`: fails CI on any pull
  request or push that touches `src/`, `tests/`, `db/migrations/`, or a
  `.csproj`/`.sln` file without also touching this file in the same diff.
- Added `.githooks/pre-commit`: mirrors the same check locally before a
  commit is even created. Not enabled by default — each clone must run
  `git config core.hooksPath .githooks` once.
- Linked both from `README.md` under Contributing.
- Tooling-only change (no `src/`/`tests/`/`db/` touched), so it is exempt
  from its own rule — this entry exists anyway for traceability.
- Not done: GitHub branch protection requiring the new CI check to pass
  before merge. That requires the branch to already exist on the remote,
  and nothing in this repo has been pushed yet (standing constraint: no
  push/PR without explicit confirmation). Revisit once the branch is
  pushed.

## 2026-08-19 — Gemini function-call loop, RLS fixes, real Postgres e2e test

- **GAP-16**: Removed the consent-bypass transition in
  `ConversationStateMachine` so `add_player` can no longer be reached before
  GDPR consent is recorded.
- **GAP-01 / GAP-02**: Added the missing `tenants` RLS policy, created the
  `neytrix_app` database role with least-privilege grants, and turned on
  `FORCE ROW LEVEL SECURITY` on tenant-scoped tables.
- **GAP-03**: Implemented the real Gemini function-calling loop.
  - Added JSON schemas for all 11 tools (`GeminiToolSchemas.cs`).
  - `VertexAgentModelClient` now parses function calls from the model and
    feeds back function responses instead of stubbing a canned reply.
  - `AgentOrchestrationService` rewritten to loop tool calls until a final
    text reply, capped at 8 iterations to avoid runaway loops.
  - `ToolExecutionService` dispatches all 11 tools with pinned-context
    guardrails (tenant/session identifiers come from the server side, never
    from model-supplied arguments).
  - Deleted `EnrollmentOrchestrationService` (the old stub) in favor of the
    above. `Program.cs` rewired accordingly.
- **GAP-05**: Waitlist position is now computed from real registration
  counts per program instead of a placeholder constant.
- Added `Assessment` and `AuditLogEntry` entities plus their repositories to
  support the full conversation flow.
- **New**: end-to-end integration test
  (`tests/NeytrixAI.Tests/Integration/EndToEndEnrollmentFlowTests.cs`) that
  drives `AgentOrchestrationService.ProcessMessageAsync` through a full
  scripted conversation — greeting, guardian intake, player intake, program
  matching, registration, assessment booking, waiver, payment link — against
  a **real local Postgres database**. Only the LLM, Stripe, and Google
  Calendar are faked (`FakeAdapters.cs`, `ScriptedAgentModelClient.cs`); every
  repository, RLS policy, and SQL statement is exercised for real via
  `PostgresTestFixture.cs`. The test ends honestly at `PaymentPending`, not
  `EnrollmentComplete` — it does not exercise the Stripe webhook /
  payment-completion path.
- **Four genuine production bugs found and fixed**, none of which the 20
  pre-existing pure-domain unit tests could have caught, because none of them
  touch Dapper or Postgres:
  1. Dapper 2.1.35 has no `DateOnly` parameter support in any shipped build,
     so every `ProgramRepository`/`PlayerRepository` create/update call threw
     `NotSupportedException` — `add_player` was completely broken in
     production. Fixed with a global Dapper type handler
     (`DateOnlyTypeHandler.cs`).
  2. `ConversationSession`/`ConversationMessage` are positional records whose
     constructor parameter types (`Guid?`, `DateTimeOffset?`) don't match
     Dapper's strict per-column constructor matching, so every session/message
     read threw `InvalidOperationException`. Fixed by mapping through private
     row DTOs inside `ConversationRepository` instead of changing the public
     record shape, which preserves `with`-expression usage elsewhere.
  3. `registrations.status` is a Postgres enum (`registration_status`) but
     was being written as a bound text parameter with no cast, so
     `create_registration` failed on every call with `PostgresException
     42804`. Fixed with explicit `CAST(@Status AS registration_status)` on
     both insert and update.
  4. `Tenant.Settings` (`Dictionary<string,object>`) is backed by a `jsonb`
     column; Npgsql returns `jsonb` as a plain string without dynamic-JSON
     support enabled, and Dapper's reflection mapper can't convert that to a
     dictionary, so any tenant lookup threw `InvalidCastException`. Fixed
     with a global Dapper type handler (`JsonDictionaryTypeHandler.cs`).
- **Not done this session**: GAP-04 (`answer_faq` still uses keyword search
  over `knowledge_chunks`, not real RAG/embeddings).
- **Verification**: `dotnet test tests/NeytrixAI.Tests` — 21/21 passing (20
  previously-passing unit tests + the new e2e test). `dotnet build
  NeytrixAI.sln` — 0 errors, 4 pre-existing cosmetic `NU1603` warnings about
  `Google.Apis.Calendar.v3` version resolution, unchanged from baseline.

## 2026-08-06 — Cloud Run containerization

- Added multi-stage `Dockerfile`, `cloudbuild.yaml`, and `.dockerignore` for
  Cloud Run deployment. Non-root app user, entrypoint binds `0.0.0.0` on
  `$PORT` at container start.
- (Retroactive entry — this change predates the progress-log convention
  above; date taken from `git log` for commit `de4b00e`.)

## 2026-08-06 — Domain/infrastructure repair and initial enrollment service

- Repaired domain-aligned API and infrastructure layering.
- Added the original `EnrollmentOrchestrationService` (since replaced by the
  Gemini function-call loop above).
- (Retroactive entry — this change predates the progress-log convention
  above; date taken from `git log` for commit `eeb07f0`.)
