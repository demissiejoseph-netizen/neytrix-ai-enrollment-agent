using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeytrixAI.Api.Tools;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Services;

namespace NeytrixAI.Api.Services;

/// <summary>
/// Executes the 11 canonical tools from ToolContracts.cs against real repositories and
/// adapters. This is the ONLY place business tool calls touch the database - the model never
/// gets a direct line to persistence, only through the typed contracts here.
///
/// Two defense-in-depth layers beyond normal validation:
///  - add_player is blocked unless the guardian's GdprConsentedAt is already set, regardless
///    of what the model or the conversation state claims.
///  - once a guardian_id/player_id/registration_id becomes known for a session, it is pinned
///    into ConversationSession.ContextJson; any later tool call that supplies a different id
///    for the same session is rejected rather than executed.
/// </summary>
public sealed class ToolExecutionService : IToolExecutionService
{
    private readonly IGuardianRepository _guardians;
    private readonly IPlayerRepository _players;
    private readonly IProgramRepository _programs;
    private readonly IRegistrationRepository _registrations;
    private readonly ITenantRepository _tenants;
    private readonly IConversationRepository _conversations;
    private readonly IAssessmentRepository _assessments;
    private readonly IAuditLogRepository _auditLog;
    private readonly IStripeAdapter _stripe;
    private readonly IGoogleCalendarAdapter _calendar;
    private readonly EligibilityEngine _eligibility;
    private readonly IKnowledgeChunkRepository _knowledgeChunks;
    private readonly IEmbeddingService _embeddings;
    private readonly StripeOptions _stripeOptions;
    private readonly GoogleCalendarOptions _calendarOptions;
    private readonly ILogger<ToolExecutionService> _logger;

    public ToolExecutionService(
        IGuardianRepository guardians,
        IPlayerRepository players,
        IProgramRepository programs,
        IRegistrationRepository registrations,
        ITenantRepository tenants,
        IConversationRepository conversations,
        IAssessmentRepository assessments,
        IAuditLogRepository auditLog,
        IStripeAdapter stripe,
        IGoogleCalendarAdapter calendar,
        EligibilityEngine eligibility,
        IKnowledgeChunkRepository knowledgeChunks,
        IEmbeddingService embeddings,
        IOptions<StripeOptions> stripeOptions,
        IOptions<GoogleCalendarOptions> calendarOptions,
        ILogger<ToolExecutionService> logger)
    {
        _guardians = guardians;
        _players = players;
        _programs = programs;
        _registrations = registrations;
        _tenants = tenants;
        _conversations = conversations;
        _assessments = assessments;
        _auditLog = auditLog;
        _stripe = stripe;
        _calendar = calendar;
        _eligibility = eligibility;
        _knowledgeChunks = knowledgeChunks;
        _embeddings = embeddings;
        _stripeOptions = stripeOptions.Value;
        _calendarOptions = calendarOptions.Value;
        _logger = logger;
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        Guid tenantId, ConversationSession session, string toolName, string argsJson, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                "answer_faq" => await AnswerFaqAsync(tenantId, Parse<AnswerFaqArgs>(argsJson), ct),
                "upsert_guardian" => await UpsertGuardianAsync(tenantId, session, Parse<UpsertGuardianArgs>(argsJson), ct),
                "add_player" => await AddPlayerAsync(tenantId, session, Parse<AddPlayerArgs>(argsJson), ct),
                "match_programs" => await MatchProgramsAsync(tenantId, session, Parse<MatchProgramsArgs>(argsJson), ct),
                "get_available_slots" => await GetAvailableSlotsAsync(tenantId, Parse<GetAvailableSlotsArgs>(argsJson), ct),
                "book_assessment" => await BookAssessmentAsync(tenantId, session, Parse<BookAssessmentArgs>(argsJson), ct),
                "send_waiver" => await SendWaiverAsync(tenantId, session, Parse<SendWaiverArgs>(argsJson), ct),
                "create_payment_link" => await CreatePaymentLinkAsync(tenantId, session, Parse<CreatePaymentLinkArgs>(argsJson), ct),
                "create_registration" => await CreateRegistrationAsync(tenantId, session, Parse<CreateRegistrationArgs>(argsJson), ct),
                "escalate_to_staff" => await EscalateToStaffAsync(tenantId, session, Parse<EscalateToStaffArgs>(argsJson), ct),
                "check_registration_status" => await CheckRegistrationStatusAsync(tenantId, session, Parse<CheckRegistrationStatusArgs>(argsJson), ct),
                _ => Error("unknown_tool", $"'{toolName}' is not a recognized tool.")
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse tool arguments for {ToolName}", toolName);
            return Error("invalid_arguments", "Arguments were not valid JSON for this tool.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure executing tool {ToolName}", toolName);
            return Error("internal_error", "Something went wrong completing that action. Please try again or escalate to staff.");
        }
    }

    // ── answer_faq ──────────────────────────────────────────────
    // GAP-04: real RAG. Embeds the question (RetrievalQuery task type) and ranks knowledge_chunks
    // by cosine distance via IKnowledgeChunkRepository.SearchAsync, instead of the old ILIKE
    // keyword-match stopgap. Fails closed: if embeddings are unavailable (no Vertex config, or a
    // live call error), or nothing is close enough to trust, this escalates to staff rather than
    // fabricate an answer.
    private const double MaxRelevantFaqDistance = 0.6;

    private async Task<ToolExecutionResult> AnswerFaqAsync(Guid tenantId, AnswerFaqArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Question))
            return Error("invalid_arguments", "question is required.");

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embeddings.EmbedAsync(args.Question, EmbeddingTaskType.RetrievalQuery, ct);
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogWarning(ex, "answer_faq embedding unavailable for tenant {TenantId}; escalating to staff", tenantId);
            return Ok(new AnswerFaqResponse(
                "I'm not able to search our FAQ right now. A staff member can help with this directly.",
                0.0, true, []));
        }

        var matches = await _knowledgeChunks.SearchAsync(tenantId, queryEmbedding, new[] { "faq", "policy" }, limit: 3, ct);
        var relevant = matches.Where(m => m.Distance <= MaxRelevantFaqDistance).ToList();

        if (relevant.Count == 0)
            return Ok(new AnswerFaqResponse(
                "I couldn't find that in our FAQ. A staff member can help with this directly.",
                0.15, true, []));

        var answer = string.Join("\n\n", relevant.Select(m => m.Content));
        var confidence = Math.Clamp(1.0 - relevant.Average(m => m.Distance), 0.0, 1.0);
        return Ok(new AnswerFaqResponse(answer, confidence, false, relevant.Select(m => m.Id.ToString()).ToArray()));
    }

    // ── upsert_guardian ──────────────────────────────────────────
    private async Task<ToolExecutionResult> UpsertGuardianAsync(Guid tenantId, ConversationSession session, UpsertGuardianArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.FirstName) || string.IsNullOrWhiteSpace(args.LastName) ||
            string.IsNullOrWhiteSpace(args.Email) || args.GdprConsentGiven is null)
            return Error("invalid_arguments", "first_name, last_name, email, and gdpr_consent_given are all required.");

        var contract = new UpsertGuardianRequest(session.SessionToken, args.FirstName, args.LastName, args.Email, args.Phone, args.GdprConsentGiven.Value);
        var violations = Validate(contract);
        if (violations.Count > 0)
            return Error("invalid_arguments", string.Join(" ", violations));

        var existing = await _guardians.GetByEmailAsync(tenantId, contract.Email, ct);
        var isNew = existing is null;
        Guardian guardian;

        if (existing is null)
        {
            guardian = Guardian.Create(tenantId, contract.FirstName, contract.LastName, contract.Email, contract.Phone);
            if (contract.GdprConsentGiven)
                guardian.RecordGdprConsent();
            await _guardians.CreateAsync(guardian, ct);
        }
        else
        {
            existing.UpdateContact(contract.Phone, "email");
            if (contract.GdprConsentGiven && existing.GdprConsentedAt is null)
                existing.RecordGdprConsent();
            await _guardians.UpdateAsync(existing, ct);
            guardian = existing;
        }

        var updatedSession = await PinContextAsync(session, "guardian_id", guardian.Id, ct);
        return Ok(new UpsertGuardianResponse(guardian.Id, isNew, guardian.FullName), updatedSession);
    }

    // ── add_player ───────────────────────────────────────────────
    private async Task<ToolExecutionResult> AddPlayerAsync(Guid tenantId, ConversationSession session, AddPlayerArgs args, CancellationToken ct)
    {
        if (args.GuardianId is null || string.IsNullOrWhiteSpace(args.FirstName) ||
            string.IsNullOrWhiteSpace(args.LastName) || args.DateOfBirth is null)
            return Error("invalid_arguments", "guardian_id, first_name, last_name, and date_of_birth are all required.");

        if (!TryCheckPinnedId(session, "guardian_id", args.GuardianId.Value, out var pinError))
            return Error("session_mismatch", pinError!);

        var contract = new AddPlayerRequest(session.SessionToken, args.GuardianId.Value, args.FirstName, args.LastName, args.DateOfBirth.Value, args.Gender);
        var violations = Validate(contract);
        if (violations.Count > 0)
            return Error("invalid_arguments", string.Join(" ", violations));

        var guardian = await _guardians.GetByIdAsync(tenantId, contract.GuardianId, ct);
        if (guardian is null)
            return Error("guardian_not_found", "No guardian with that id exists for this tenant.");
        if (guardian.GdprConsentedAt is null)
            return Error("consent_required", "This guardian has not given GDPR consent yet; add_player is blocked until upsert_guardian records consent.");

        Player player;
        try
        {
            player = Player.Create(tenantId, guardian.Id, contract.FirstName, contract.LastName, contract.DateOfBirth, contract.Gender);
        }
        catch (ArgumentException ex)
        {
            return Error("invalid_arguments", ex.Message);
        }

        await _players.CreateAsync(player, ct);
        var updatedSession = await PinContextAsync(session, "player_id", player.Id, ct);
        return Ok(new AddPlayerResponse(player.Id, player.FullName, player.CurrentAge), updatedSession);
    }

    // ── match_programs ───────────────────────────────────────────
    private async Task<ToolExecutionResult> MatchProgramsAsync(Guid tenantId, ConversationSession session, MatchProgramsArgs args, CancellationToken ct)
    {
        if (args.PlayerId is null)
            return Error("invalid_arguments", "player_id is required.");
        if (!TryCheckPinnedId(session, "player_id", args.PlayerId.Value, out var pinError))
            return Error("session_mismatch", pinError!);

        var player = await _players.GetByIdAsync(tenantId, args.PlayerId.Value, ct);
        if (player is null)
            return Error("player_not_found", "No player with that id exists for this tenant.");

        var programs = (await _programs.GetByTenantAsync(tenantId, ct)).Where(p => p.IsActive).ToList();
        var counts = new Dictionary<Guid, int>();
        foreach (var program in programs)
            counts[program.Id] = CountOccupied(await _registrations.GetByProgramAsync(tenantId, program.Id, ct));

        var matches = _eligibility.MatchPrograms(player, programs, counts);
        var dtos = matches.Select(m => new ProgramMatchDto(
            m.Program.Id, m.Program.Name, m.Program.Sport, m.Program.MinAgeYears, m.Program.MaxAgeYears,
            m.Program.GenderPolicy, m.Program.SkillLevel, m.EligibilityResult.SpotsRemaining,
            m.EligibilityResult.Status == EligibilityStatus.WaitlistOnly, m.Program.PriceCents, m.Program.DepositCents,
            m.Program.Currency, m.Program.StartDate, m.Program.EndDate, m.Program.Location, m.RelevanceScore)).ToArray();

        return Ok(new MatchProgramsResponse(dtos, dtos.Length));
    }

    // ── get_available_slots ──────────────────────────────────────
    private async Task<ToolExecutionResult> GetAvailableSlotsAsync(Guid tenantId, GetAvailableSlotsArgs args, CancellationToken ct)
    {
        if (args.ProgramId is null)
            return Error("invalid_arguments", "program_id is required.");

        var tenant = await _tenants.GetByIdAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(tenant?.GoogleCalendarId))
            return Error("calendar_not_configured", "This organization has not connected a Google Calendar yet.");

        var program = await _programs.GetByIdAsync(tenantId, args.ProgramId.Value, ct);
        if (program is null)
            return Error("program_not_found", "No program with that id exists for this tenant.");

        var weekOf = args.PreferredWeekOf ?? NextMonday(DateOnly.FromDateTime(DateTime.UtcNow));
        var slots = await _calendar.GetAvailableSlotsAsync(tenant.GoogleCalendarId, weekOf, _calendarOptions.DefaultAssessmentDurationMinutes, ct);
        var dtos = slots.Select(s => new SlotDto(s.SlotId, s.StartsAt, s.EndsAt, s.DurationMinutes, s.Location ?? program.Location)).ToArray();

        return Ok(new GetAvailableSlotsResponse(dtos));
    }

    // ── book_assessment ──────────────────────────────────────────
    private async Task<ToolExecutionResult> BookAssessmentAsync(Guid tenantId, ConversationSession session, BookAssessmentArgs args, CancellationToken ct)
    {
        if (args.RegistrationId is null || string.IsNullOrWhiteSpace(args.SlotId))
            return Error("invalid_arguments", "registration_id and slot_id are both required.");
        if (!TryCheckPinnedId(session, "registration_id", args.RegistrationId.Value, out var pinError))
            return Error("session_mismatch", pinError!);

        var registration = await _registrations.GetByIdAsync(tenantId, args.RegistrationId.Value, ct);
        if (registration is null)
            return Error("registration_not_found", "No registration with that id exists for this tenant.");

        var tenant = await _tenants.GetByIdAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(tenant?.GoogleCalendarId))
            return Error("calendar_not_configured", "This organization has not connected a Google Calendar yet.");

        var guardian = await _guardians.GetByIdAsync(tenantId, registration.GuardianId, ct);
        var player = await _players.GetByIdAsync(tenantId, registration.PlayerId, ct);
        var program = await _programs.GetByIdAsync(tenantId, registration.ProgramId, ct);
        if (guardian is null || player is null || program is null)
            return Error("data_inconsistent", "Could not load the guardian, player, or program linked to this registration.");

        BookedEvent booked;
        try
        {
            booked = await _calendar.BookSlotAsync(tenant.GoogleCalendarId, args.SlotId, guardian.FullName, guardian.Email, player.FullName, program.Name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calendar booking failed for registration {RegistrationId}", registration.Id);
            return Error("booking_failed", "Could not book that slot - it may no longer be available. Try get_available_slots again.");
        }

        var assessment = Assessment.Create(tenantId, registration.Id, booked.StartsAt, booked.EventId, _calendarOptions.DefaultAssessmentDurationMinutes, program.Location);
        await _assessments.CreateAsync(assessment, ct);

        return Ok(new BookAssessmentResponse(assessment.Id, booked.StartsAt, program.Location, booked.EventId,
            $"Assessment booked for {player.FullName} on {booked.StartsAt:f}."));
    }

    // ── send_waiver ──────────────────────────────────────────────
    private async Task<ToolExecutionResult> SendWaiverAsync(Guid tenantId, ConversationSession session, SendWaiverArgs args, CancellationToken ct)
    {
        if (args.RegistrationId is null)
            return Error("invalid_arguments", "registration_id is required.");
        if (!TryCheckPinnedId(session, "registration_id", args.RegistrationId.Value, out var pinError))
            return Error("session_mismatch", pinError!);

        var registration = await _registrations.GetByIdAsync(tenantId, args.RegistrationId.Value, ct);
        if (registration is null)
            return Error("registration_not_found", "No registration with that id exists for this tenant.");

        var guardian = await _guardians.GetByIdAsync(tenantId, registration.GuardianId, ct);
        if (guardian is null)
            return Error("data_inconsistent", "Could not load the guardian for this registration.");

        var result = await _stripe.CreateWaiverLinkAsync(registration.Id, guardian.Email, ct);
        registration.MarkWaiverSent();
        await _registrations.UpdateAsync(registration, ct);

        return Ok(new SendWaiverResponse(true, result.WaiverUrl, result.ExpiresAt));
    }

    // ── create_payment_link ──────────────────────────────────────
    private async Task<ToolExecutionResult> CreatePaymentLinkAsync(Guid tenantId, ConversationSession session, CreatePaymentLinkArgs args, CancellationToken ct)
    {
        if (args.RegistrationId is null)
            return Error("invalid_arguments", "registration_id is required.");
        if (!TryCheckPinnedId(session, "registration_id", args.RegistrationId.Value, out var pinError))
            return Error("session_mismatch", pinError!);

        var registration = await _registrations.GetByIdAsync(tenantId, args.RegistrationId.Value, ct);
        if (registration is null)
            return Error("registration_not_found", "No registration with that id exists for this tenant.");

        var tenant = await _tenants.GetByIdAsync(tenantId, ct);
        if (string.IsNullOrWhiteSpace(tenant?.StripeAccountId))
            return Error("stripe_not_configured", "This organization has not connected Stripe yet.");

        var program = await _programs.GetByIdAsync(tenantId, registration.ProgramId, ct);
        if (program is null)
            return Error("data_inconsistent", "Could not load the program for this registration.");

        var depositOnly = args.DepositOnly ?? false;
        var amountCents = depositOnly ? program.DepositCents : program.PriceCents;
        if (depositOnly && amountCents <= 0)
            return Error("deposit_not_configured", "This program does not have a deposit amount configured.");
        if (amountCents <= 0)
            return Error("invalid_price", "This program has no price configured.");

        var successUrl = _stripeOptions.SuccessUrlTemplate.Replace("{registrationId}", registration.Id.ToString());
        var cancelUrl = _stripeOptions.CancelUrlTemplate.Replace("{registrationId}", registration.Id.ToString());

        var result = await _stripe.CreateCheckoutSessionAsync(tenant.StripeAccountId!, registration.Id, amountCents, program.Currency, successUrl, cancelUrl, depositOnly, ct);
        registration.AttachCheckoutSession(result.CheckoutSessionId);
        await _registrations.UpdateAsync(registration, ct);

        return Ok(new CreatePaymentLinkResponse(result.PaymentUrl, result.AmountCents, result.Currency, result.CheckoutSessionId, result.ExpiresAt));
    }

    // ── create_registration ──────────────────────────────────────
    private async Task<ToolExecutionResult> CreateRegistrationAsync(Guid tenantId, ConversationSession session, CreateRegistrationArgs args, CancellationToken ct)
    {
        if (args.GuardianId is null || args.PlayerId is null || args.ProgramId is null)
            return Error("invalid_arguments", "guardian_id, player_id, and program_id are all required.");
        if (!TryCheckPinnedId(session, "guardian_id", args.GuardianId.Value, out var g1)) return Error("session_mismatch", g1!);
        if (!TryCheckPinnedId(session, "player_id", args.PlayerId.Value, out var g2)) return Error("session_mismatch", g2!);

        var guardian = await _guardians.GetByIdAsync(tenantId, args.GuardianId.Value, ct);
        var player = await _players.GetByIdAsync(tenantId, args.PlayerId.Value, ct);
        var program = await _programs.GetByIdAsync(tenantId, args.ProgramId.Value, ct);
        if (guardian is null) return Error("guardian_not_found", "No guardian with that id exists for this tenant.");
        if (player is null) return Error("player_not_found", "No player with that id exists for this tenant.");
        if (program is null) return Error("program_not_found", "No program with that id exists for this tenant.");

        if (await _registrations.ExistsAsync(tenantId, player.Id, program.Id, ct))
            return Error("already_registered", "This player already has a registration for this program.");

        var existingForProgram = (await _registrations.GetByProgramAsync(tenantId, program.Id, ct)).ToList();
        var occupied = CountOccupied(existingForProgram);
        var eligibility = _eligibility.CheckEligibility(player, program, occupied);
        if (eligibility.Status == EligibilityStatus.Ineligible)
            return Error("ineligible", string.Join(" ", eligibility.FailureReasons));

        var waitlisted = eligibility.Status == EligibilityStatus.WaitlistOnly;
        int? waitlistPosition = waitlisted
            ? existingForProgram.Count(r => r.Status == Registration.StatusWaitlisted) + 1
            : null;

        var registration = Registration.Create(tenantId, guardian.Id, player.Id, program.Id, waitlisted, waitlistPosition);
        await _registrations.CreateAsync(registration, ct);

        var updatedSession = await PinContextAsync(session, "registration_id", registration.Id, ct);
        return Ok(new CreateRegistrationResponse(registration.Id, registration.Status, waitlisted, waitlistPosition), updatedSession);
    }

    // ── escalate_to_staff ─────────────────────────────────────────
    // NOTE: this appends a real audit_log row (previously zero references existed to that
    // table) but there is still no live notification channel (email/Slack/etc.) wired up -
    // "a staff member will follow up" is a promise the audit trail supports finding, not one
    // this call itself fulfils by paging anyone.
    private async Task<ToolExecutionResult> EscalateToStaffAsync(Guid tenantId, ConversationSession session, EscalateToStaffArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Reason))
            return Error("invalid_arguments", "reason is required.");

        var category = Enum.TryParse<EscalationCategory>(args.Category, ignoreCase: true, out var parsed) ? parsed : EscalationCategory.General;
        var ticketId = $"ESC-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var eta = category is EscalationCategory.Safeguarding or EscalationCategory.Medical ? "within 2 hours" : "within 1 business day";

        var payload = JsonSerializer.Serialize(new { ticket_id = ticketId, reason = args.Reason, category = category.ToString() }, ToolJsonOptions.Model);
        await _auditLog.AppendAsync(AuditLogEntry.Create(tenantId, "escalate_to_staff", "conversation_session", session.Id, payload), ct);

        return Ok(new EscalateToStaffResponse(ticketId, "A staff member will follow up with you.", eta));
    }

    // ── check_registration_status ────────────────────────────────
    private async Task<ToolExecutionResult> CheckRegistrationStatusAsync(Guid tenantId, ConversationSession session, CheckRegistrationStatusArgs args, CancellationToken ct)
    {
        if (args.RegistrationId is null)
            return Error("invalid_arguments", "registration_id is required.");
        if (!TryCheckPinnedId(session, "registration_id", args.RegistrationId.Value, out var pinError))
            return Error("session_mismatch", pinError!);

        var registration = await _registrations.GetByIdAsync(tenantId, args.RegistrationId.Value, ct);
        if (registration is null)
            return Error("registration_not_found", "No registration with that id exists for this tenant.");

        var program = await _programs.GetByIdAsync(tenantId, registration.ProgramId, ct);
        var paymentComplete = program is not null && registration.AmountPaidCents >= program.PriceCents;

        return Ok(new CheckRegistrationStatusResponse(registration.Status, registration.WaiverSigned, paymentComplete, registration.IsEnrolled, registration.WaitlistPosition));
    }

    // ── shared helpers ────────────────────────────────────────────
    private static int CountOccupied(IEnumerable<Registration> registrations) =>
        registrations.Count(r => r.Status is not (Registration.StatusCancelled or Registration.StatusWaitlisted));

    private static DateOnly NextMonday(DateOnly from)
    {
        var diff = ((int)DayOfWeek.Monday - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(diff == 0 ? 7 : diff);
    }

    private static List<string> Validate(object contract)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(contract, new ValidationContext(contract), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid value.").ToList();
    }

    private static T Parse<T>(string argsJson) where T : class
    {
        var json = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson;
        return JsonSerializer.Deserialize<T>(json, ToolJsonOptions.Model)
               ?? throw new JsonException($"Arguments for {typeof(T).Name} deserialized to null.");
    }

    private static ToolExecutionResult Ok<T>(T response, ConversationSession? updatedSession = null) =>
        new(true, JsonSerializer.Serialize(response, ToolJsonOptions.Model), updatedSession);

    private static ToolExecutionResult Error(string code, string message) =>
        new(false, JsonSerializer.Serialize(new { error = code, message }, ToolJsonOptions.Model));

    /// <summary>Checks a supplied id against any id already pinned in this session's context for the same key.</summary>
    private static bool TryCheckPinnedId(ConversationSession session, string key, Guid suppliedId, out string? error)
    {
        error = null;
        var context = ParseContext(session.ContextJson);
        if (context.TryGetPropertyValue(key, out var node) && node is System.Text.Json.Nodes.JsonValue value &&
            value.TryGetValue(out string? pinnedText) && Guid.TryParse(pinnedText, out var pinned) && pinned != suppliedId)
        {
            error = $"This session is already tied to a different {key}; the id supplied does not match and was rejected for safety.";
            return false;
        }
        return true;
    }

    private async Task<ConversationSession> PinContextAsync(ConversationSession session, string key, Guid id, CancellationToken ct)
    {
        var context = ParseContext(session.ContextJson);
        context[key] = id.ToString();
        var updated = session with { ContextJson = context.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow };
        await _conversations.UpdateSessionAsync(updated, ct);
        return updated;
    }

    private static System.Text.Json.Nodes.JsonObject ParseContext(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return new System.Text.Json.Nodes.JsonObject();
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(contextJson) as System.Text.Json.Nodes.JsonObject
                   ?? new System.Text.Json.Nodes.JsonObject();
        }
        catch (JsonException)
        {
            return new System.Text.Json.Nodes.JsonObject();
        }
    }

    // ── model-facing argument DTOs (SessionToken excluded; server injects it) ──
    private sealed record AnswerFaqArgs(string? Question);
    private sealed record UpsertGuardianArgs(string? FirstName, string? LastName, string? Email, string? Phone, bool? GdprConsentGiven);
    private sealed record AddPlayerArgs(Guid? GuardianId, string? FirstName, string? LastName, DateOnly? DateOfBirth, string? Gender);
    private sealed record MatchProgramsArgs(Guid? PlayerId);
    private sealed record GetAvailableSlotsArgs(Guid? ProgramId, DateOnly? PreferredWeekOf);
    private sealed record BookAssessmentArgs(Guid? RegistrationId, string? SlotId);
    private sealed record SendWaiverArgs(Guid? RegistrationId);
    private sealed record CreatePaymentLinkArgs(Guid? RegistrationId, bool? DepositOnly);
    private sealed record CreateRegistrationArgs(Guid? GuardianId, Guid? PlayerId, Guid? ProgramId);
    private sealed record EscalateToStaffArgs(string? Reason, string? Category);
    private sealed record CheckRegistrationStatusArgs(Guid? RegistrationId);
}
