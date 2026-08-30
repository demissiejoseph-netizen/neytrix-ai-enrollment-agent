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

## 2026-08-29 — Stripe webhook end-to-end test + real tenant-resolution bug fix

- **Bug found (production-impacting, not just a test gap)**: The real Stripe
  webhook handler, `AgentOrchestrationService.HandleStripeWebhookAsync`,
  resolved the paid registration with
  `_registrations.GetByIdAsync(Guid.Empty, registrationId, ct)` because
  Stripe's checkout metadata only ever carried `registration_id` — there was
  no tenant context available inside the webhook. `DbConnectionFactory
  .CreateConnectionAsync` only runs `SELECT set_config('app.tenant_id', ...)`
  `if (tenantId != Guid.Empty)`, so calling it with `Guid.Empty` left
  `app.tenant_id` completely unset for that connection. The RLS policies in
  `db/migrations/001_initial_schema.sql` gate every row with
  `USING (tenant_id = current_setting('app.tenant_id', true)::UUID)`, and
  `current_setting(..., true)` returns `NULL` when unset — so the predicate
  evaluated to `NULL` (never `TRUE`) against a real RLS-enforced database.
  **The webhook's registration lookup would always return zero rows in
  production**, meaning a real customer's payment would complete on Stripe's
  side but silently never be recorded — the registration would stay stuck at
  `payment_pending` forever with no error surfaced anywhere. This was a live
  bug, not merely "untested" behavior — it just happened to be invisible
  because the existing e2e test used `FakeStripeAdapter` and never exercised
  the webhook at all.
- **Fix**: Threaded a real `tenant_id` through Stripe's own metadata instead
  of trying to guess it inside the webhook:
  - `IStripeAdapter.CreateCheckoutSessionAsync` / `StripeAdapter
    .CreateCheckoutSessionAsync` now take a `Guid tenantId` parameter and add
    `["tenant_id"] = tenantId.ToString()` to the Stripe Checkout session's
    `Metadata` alongside the existing `registration_id`/`deposit_only`.
  - `ToolExecutionService.CreatePaymentLinkAsync` passes the tenant id it
    already has in scope into the new parameter.
  - `AgentOrchestrationService.HandleStripeWebhookAsync` now reads
    `tenant_id` back out of `checkout.Metadata`, `Guid.TryParse`s it, and
    uses that real tenant for both the registration and program lookups.
    Fails closed (returns without enrolling, no exception) if `tenant_id` is
    missing, unparseable, `Guid.Empty`, or doesn't match the tenant the
    resolved registration actually belongs to — mirroring the existing
    fail-closed pattern for a missing/invalid `registration_id`.
  - `FakeStripeAdapter.CreateCheckoutSessionAsync` (test double) updated to
    match the new signature.
- **New test coverage**: Added
  `tests/NeytrixAI.Tests/Integration/StripeWebhookTests.cs`, which seeds a
  real tenant/guardian/player/program/registration in Postgres (reaching
  `payment_pending` with a signed waiver, mirroring
  `EndToEndEnrollmentFlowTests`'s setup), builds a genuine HMAC-SHA256-signed
  `checkout.session.completed` webhook payload (verified against the actual
  Stripe.net 45.14.0 `EventConverter` deserialization requirements — a
  payload missing the top-level `request` field throws a
  `NullReferenceException` inside `Stripe.Infrastructure.EventConverter
  .ReadJson`, which unconditionally reads `jsonObject["request"].Type`), and
  calls the real `AgentOrchestrationService.HandleStripeWebhookAsync` with a
  real `StripeAdapter` — not a fake — for real signature verification, real
  tenant resolution, and a real RLS-scoped Postgres write. Four cases:
  1. A correctly-signed event with matching `tenant_id`/`registration_id`
     enrolls the registration (`Status` → `enrolled`, `AmountPaidCents`,
     `StripePaymentIntentId`, `EnrolledAt` all set correctly).
  2. A forged/tampered signature is rejected by real HMAC verification
     (`StripeException` thrown) and leaves the registration untouched.
  3. An event with no `tenant_id` in metadata (the old, pre-fix shape) is
     dropped without enrolling and without throwing.
  4. An event whose `tenant_id` doesn't match the registration's real tenant
     is dropped without enrolling and without throwing.
  This directly closes the gap called out in `EndToEndEnrollmentFlowTests`,
  which intentionally stops at `PaymentPending` because it never simulates
  the webhook — real payment completion was previously untested end to end.
- **Not done this session**: No change to `ChatController.StripeWebhook`'s
  error handling — a bad signature still propagates as an unhandled
  exception to ASP.NET Core's default 500 response rather than a structured
  4xx. Stripe's retry semantics tolerate this, but a follow-up could catch
  `StripeException` there and return a deliberate `400` instead.
- **Verification**: `dotnet build NeytrixAI.sln` — 0 errors (same 6
  pre-existing cosmetic `NU1603` warnings about `Google.Apis.Calendar.v3`
  version resolution, unchanged from baseline). `dotnet test
  tests/NeytrixAI.Tests` — 28/28 passing (24 previously-passing + the 4 new
  Stripe webhook tests).

## 2026-08-19 — GAP-04: real RAG/embeddings for answer_faq

- **What changed and why**: Replaced `answer_faq`'s ILIKE keyword-search
  stopgap with real vector-similarity retrieval over `knowledge_chunks`,
  closing GAP-04. `knowledge_chunks.embedding vector(1536)` and its
  `ivfflat vector_cosine_ops` index already existed in the schema but were
  never populated or queried — this wires up the whole path end to end.
  - `Pgvector.Npgsql`'s `UseVector()` extension only registers its type
    mapping on a specific `NpgsqlDataSource` (Npgsql 8+ removed the old
    process-wide `GlobalTypeMapper`), so `DbConnectionFactory` now builds
    one `NpgsqlDataSourceBuilder(...).UseVector().Build()` in its
    constructor and hands out connections via
    `_dataSource.OpenConnectionAsync(...)` instead of bare
    `new NpgsqlConnection(...)`. Its DI registration moved from `AddScoped`
    to `AddSingleton` accordingly — rebuilding a whole connection pool per
    request scope would defeat pooling and is unnecessary since
    `NpgsqlDataSource` is itself thread-safe. Added a Dapper
    `SqlMapper.TypeHandler<Pgvector.Vector>` (`VectorTypeHandler.cs`) so
    `Pgvector.Vector` params/results round-trip through Dapper without
    Dapper trying to expand the vector's `IEnumerable<float>` into an
    `IN (...)` list.
  - New `IEmbeddingService` (`EmbedAsync`/`EmbedBatchAsync`,
    `EmbeddingTaskType.RetrievalQuery`/`RetrievalDocument`), following the
    same real-vs-fail-closed pattern as `IAgentModelClient`:
    `VertexEmbeddingService` calls Vertex AI's `gemini-embedding-001` via
    `PredictionServiceClient.PredictAsync` (this model accepts only one
    instance per Predict request, so batch embedding loops one call per
    text — confirmed against Vertex AI's published API docs, not assumed);
    `NullEmbeddingService` throws `EmbeddingUnavailableException` rather
    than fabricate a vector when `VertexAI:ProjectId` isn't configured.
    `VertexAI:EmbeddingModel` (default `gemini-embedding-001`) and
    `VertexAI:EmbeddingDimensions` (default `1536`, must match the schema
    column width) added to `appsettings*.json`.
  - New `IKnowledgeChunkRepository`/`KnowledgeChunkRepository`: `CreateAsync`
    for ingestion, `SearchAsync` ranks by cosine distance
    (`embedding <=> @QueryEmbedding`) matching the existing ivfflat index's
    operator class. New `KnowledgeChunk` domain entity (embedding as a
    plain `float[]` at the Domain boundary, keeping the Domain project free
    of the pgvector/Npgsql package reference).
  - New `IKnowledgeIngestionService`/`KnowledgeIngestionService`: embeds
    content with the `RetrievalDocument` task type and stores it via the
    repository. `knowledge_chunks` ships empty with no seed data anywhere in
    the repo, so this load-time path (not one of the 11 canonical
    model-facing tools) is what an operator/seed script uses to populate a
    tenant's FAQ/policy content — without it, GAP-04 would be unexercisable
    even though "complete".
  - `ToolExecutionService.AnswerFaqAsync` rewritten: embeds the question
    (`RetrievalQuery`), calls `IKnowledgeChunkRepository.SearchAsync` over
    `source_type IN ('faq','policy')`, keeps matches with cosine distance
    \<= 0.6, derives `ConfidenceScore` from `1 - distance`. Fails closed on
    both axes: an `EmbeddingUnavailableException` (Vertex not configured or
    a live call error) and "nothing close enough" both return
    `RequiresEscalation: true` with a staff-handoff message rather than
    guessing. `ToolExecutionService`'s constructor swapped its raw
    `IDbConnectionFactory` dependency (only ever used for the old keyword
    query) for `IKnowledgeChunkRepository`/`IEmbeddingService`.
- **Not done this session**: no bulk/CLI ingestion tool for operators to
  load a tenant's real FAQ content in production — `IKnowledgeIngestionService`
  exists and is exercised by tests, but nothing calls it outside test code
  yet. The Stripe webhook/payment-completion gap remains separately open
  and untouched.
- **Verification**: `dotnet build NeytrixAI.sln` — 0 errors (one
  `CS0104` ambiguous-`Value`-type build error along the way, from
  `Google.Cloud.AIPlatform.V1.Value` vs `Google.Protobuf.WellKnownTypes.Value`
  both being in scope, fixed with a `using Value = ...` alias). `dotnet test
  tests/NeytrixAI.Tests` — 24/24 passing (21 previously-passing + 3 new
  `AnswerFaqRagTests` cases covering: a close-paraphrase question correctly
  retrieves the matching seeded chunk and not an unrelated one via a real
  pgvector `ORDER BY embedding <=> ...` query; a question matching nothing
  escalates instead of fabricating an answer; embeddings being unavailable
  (`NullEmbeddingService`) also escalates gracefully instead of throwing).
  The RAG tests use a deterministic hashed-bag-of-words `FakeEmbeddingService`
  test double (no live Vertex AI calls in CI/tests) but exercise the real
  local Postgres, real `knowledge_chunks` table, and real ivfflat-indexed
  cosine-distance query end to end — same pattern as the existing e2e test
  faking Stripe/Calendar but never the database. Extracted the e2e test's
  private `IsPostgresReachableAsync` soft-skip helper into
  `PostgresTestFixture` as a public static method so the new test class
  could reuse it instead of duplicating it.

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
