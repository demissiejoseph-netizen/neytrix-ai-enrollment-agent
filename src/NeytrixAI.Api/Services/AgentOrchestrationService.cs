using System.Collections.Concurrent;
using NeytrixAI.Api.Controllers;
using NeytrixAI.Api.Middleware;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Auth;
using NeytrixAI.Infrastructure.Services;

namespace NeytrixAI.Api.Services;

/// <summary>
/// Deterministic, fail-closed conversation driver. It does NOT require an LLM to
/// run: the enrollment workflow is scripted through the
/// <see cref="ConversationStateMachine"/> so required steps (guardian intake,
/// GDPR consent, player intake) can never be skipped. An optional LLM layer can
/// later be plugged in for free-form FAQ answering, but it may only call the
/// same permissioned tools — it can never bypass the state machine or the
/// eligibility/consent gates below.
///
/// Sessions are held in-memory (single-instance). Durable, multi-instance
/// session storage (conversation_sessions table) is a documented follow-up.
/// </summary>
public sealed class AgentOrchestrationService : IAgentOrchestrationService
{
    private static readonly ConcurrentDictionary<string, Session> Sessions = new();

    private readonly IGuardianRepository _guardians;
    private readonly IPlayerRepository _players;
    private readonly EnrollmentOrchestrationService _enrollment;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AgentOrchestrationService> _logger;

    public AgentOrchestrationService(
        IGuardianRepository guardians,
        IPlayerRepository players,
        EnrollmentOrchestrationService enrollment,
        IHttpContextAccessor http,
        ILogger<AgentOrchestrationService> logger)
    {
        _guardians = guardians;
        _players = players;
        _enrollment = enrollment;
        _http = http;
        _logger = logger;
    }

    private Guid RequireTenantId()
    {
        if (_http.HttpContext?.Items.TryGetValue("TenantId", out var value) == true && value is Guid tenantId)
            return tenantId;
        throw new InvalidOperationException("Tenant context is not resolved.");
    }

    // Optional Clerk identity for this request, populated by
    // ClerkAuthenticationMiddleware. Null for the anonymous flow.
    private ClerkIdentity? TryGetClerkIdentity()
    {
        if (_http.HttpContext?.Items.TryGetValue(ClerkAuthenticationMiddleware.ClerkIdentityItemKey, out var value) == true
            && value is ClerkIdentity identity)
            return identity;
        return null;
    }

    public async Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken ct)
    {
        var tenantId = RequireTenantId();
        var token = Guid.NewGuid().ToString("N");
        var session = new Session { Token = token, TenantId = tenantId, GuardianEmail = request.GuardianEmail };

        // OPTIONAL Clerk linkage. If the widget attached a verified Clerk session
        // token, tie this conversation to the guardian's identity from the start.
        // Everything here is additive — with no Clerk identity the session is
        // exactly the anonymous session it always was.
        var clerk = TryGetClerkIdentity();
        if (clerk is not null)
        {
            session.ClerkUserId = clerk.UserId;
            session.ClerkEmail = clerk.Email;
            session.ClerkFirstName = clerk.FirstName;
            session.ClerkLastName = clerk.LastName;

            // Returning Clerk guardian? They already have a (consented) row, so link
            // the session to it immediately. A brand-new Clerk user has no row yet:
            // we deliberately do NOT fabricate one here because the fail-closed GDPR
            // write gate forbids storing a guardian before consent. That row is
            // created — and stamped with this clerk_user_id — during the normal
            // consent step of INTAKE (see CollectConsentAsync).
            var existing = await _guardians.GetByClerkUserIdAsync(tenantId, clerk.UserId, ct);
            if (existing is not null)
            {
                session.GuardianId = existing.Id;
                session.GuardianAlreadyPersisted = true;
                _logger.LogInformation(
                    "Linked session {Token} (tenant {TenantId}) to existing Clerk guardian {GuardianId} at session start.",
                    token, tenantId, existing.Id);
            }
        }

        Sessions[token] = session;

        const string greeting = "Hi! I can help you enrol your child in one of our programs. " +
            "To get started, what's the parent or guardian's full name?";
        // First (and logged) transition: leave the Greeting state for guardian intake.
        LogTransition(session, ConversationState.CollectingGuardianName, "session_started");
        session.State = ConversationState.CollectingGuardianName;

        return new StartSessionResponse(token, greeting, session.State.ToString());
    }

    public Task<SessionStateResponse?> GetSessionStateAsync(string sessionToken, CancellationToken ct)
    {
        if (!Sessions.TryGetValue(sessionToken, out var s))
            return Task.FromResult<SessionStateResponse?>(null);

        return Task.FromResult<SessionStateResponse?>(new SessionStateResponse(
            s.Token, s.State.ToString(), ConversationStateMachine.IsTerminal(s.State),
            s.GuardianId, s.PlayerId, s.RegistrationId));
    }

    public async Task<ChatMessageResponse?> ProcessMessageAsync(string sessionToken, SendMessageRequest request, CancellationToken ct)
    {
        if (!Sessions.TryGetValue(sessionToken, out var s))
            return null;

        if (ConversationStateMachine.IsTerminal(s.State))
            return Reply(s, "This conversation has ended. If you still need help, please start a new session and we'll pick things up from there.");

        // ── STEP 1: Safety triage FIRST, before any parsing/extraction ──────────
        // Safety triage must run to completion on the raw guardian message before a
        // single intent-parse or data-extraction step touches it. A message that
        // both raises a safety signal AND carries enrolment data (e.g. "he has a
        // medical condition, DOB 2015-06-01") must escalate — the DOB is NEVER
        // extracted first. Triage therefore reads request.Content directly, ahead
        // of the trimmed `message` and the state dispatch below.
        if (SafetyTriage.ShouldEscalate(request.Content, out var category))
            return Escalate(s, category, $"Safety triage matched a '{category}' signal in guardian message.");

        // ── STEP 2: only now do we parse/extract from the message ───────────────
        var message = (request.Content ?? string.Empty).Trim();

        try
        {
            return s.State switch
            {
                ConversationState.CollectingGuardianName => CollectGuardianName(s, message),
                ConversationState.CollectingGuardianEmail => CollectGuardianEmail(s, message),
                ConversationState.CollectingGuardianPhone => CollectGuardianPhone(s, message),
                ConversationState.CollectingGdprConsent => await CollectConsentAsync(s, message, ct),
                ConversationState.CollectingPlayerName => CollectPlayerName(s, message),
                ConversationState.CollectingPlayerDob => CollectPlayerDob(s, message),
                ConversationState.CollectingPlayerGender => await CollectPlayerGenderAsync(s, message, ct),
                ConversationState.ShowingProgramMatches => Reply(s,
                    "Let me know the name of the program you'd like to enrol in, or ask me any question about them."),
                _ => Escalate(s, EscalationReason.TechnicalFailure,
                    $"No handler for conversation state {s.State}.")
            };
        }
        catch (Exception ex)
        {
            // Fail loud, not silent: an unexpected/malformed condition escalates to a
            // human rather than leaving the guardian in an undefined automated state.
            _logger.LogError(ex, "Orchestration error in state {State}; escalating.", s.State);
            return Escalate(s, EscalationReason.TechnicalFailure,
                $"Unhandled orchestration exception in state {s.State}.");
        }
    }

    private ChatMessageResponse CollectGuardianName(Session s, string message)
    {
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return Reply(s, "Thanks! Could you share both a first and last name for the parent or guardian?");

        s.GuardianFirstName = parts[0];
        s.GuardianLastName = parts[1];
        return Advance(s, ConversationState.CollectingGuardianEmail, "guardian_name_provided",
            $"Lovely to meet you, {s.GuardianFirstName}! What's the best email address to reach you on?");
    }

    private ChatMessageResponse CollectGuardianEmail(Session s, string message)
    {
        if (!IsValidEmail(message))
            return Reply(s, "Hmm, that doesn't look quite like an email address. Could you double-check and type it again?");

        s.GuardianEmail = message.ToLowerInvariant();
        return Advance(s, ConversationState.CollectingGuardianPhone, "guardian_email_provided",
            "Great, thank you. And is there a contact phone number we can reach you on? " +
            "If you'd rather not share one, just type 'skip'.");
    }

    private ChatMessageResponse CollectGuardianPhone(Session s, string message)
    {
        s.GuardianPhone = message.Equals("skip", StringComparison.OrdinalIgnoreCase) ? null : message;
        return Advance(s, ConversationState.CollectingGdprConsent, "guardian_phone_provided",
            "Before we go any further, I need to check one important thing. To process this enrolment we'll need to store " +
            "your details and your child's details, in line with our privacy policy. Are you happy for us to do that? " +
            "Please reply 'yes' to consent, or 'no' if you'd prefer not to.");
    }

    private async Task<ChatMessageResponse> CollectConsentAsync(Session s, string message, CancellationToken ct)
    {
        if (IsAffirmative(message))
        {
            if (s.GuardianAlreadyPersisted && s.GuardianId is not null)
            {
                // Returning Clerk-authenticated guardian: their consented row already
                // exists and the session is already linked to it (see StartSession).
                // Do NOT create a second row — the clerk_user_id UNIQUE index would
                // reject it anyway. Consent remains recorded on the existing row.
                _logger.LogInformation(
                    "Re-confirmed consent for already-linked guardian {GuardianId} in session {Token} (tenant {TenantId}).",
                    s.GuardianId, s.Token, s.TenantId);
                return Advance(s, ConversationState.CollectingPlayerName, "gdpr_consent_granted",
                    "Thank you so much. Now let's tell me about your child — what's their full name?");
            }

            // New guardian (anonymous OR first-time Clerk sign-in). When a verified
            // Clerk identity is present we stamp clerk_user_id onto the row at
            // creation, linking identity to the guardian without fabricating any
            // profile data — the name/email/phone here are the real values just
            // collected during INTAKE, not placeholders.
            var guardian = Guardian.Create(
                s.TenantId, s.GuardianFirstName!, s.GuardianLastName!, s.GuardianEmail!, s.GuardianPhone,
                clerkUserId: s.ClerkUserId);
            guardian.RecordGdprConsent();
            await _guardians.CreateAsync(guardian, ct);
            s.GuardianId = guardian.Id;
            s.GuardianAlreadyPersisted = true;
            _logger.LogInformation(
                "Recorded GDPR consent and stored guardian {GuardianId} in session {Token} (tenant {TenantId}); clerkLinked={ClerkLinked}.",
                guardian.Id, s.Token, s.TenantId, s.ClerkUserId is not null);
            return Advance(s, ConversationState.CollectingPlayerName, "gdpr_consent_granted",
                "Thank you so much. Now let's tell me about your child — what's their full name?");
        }

        if (IsNegative(message))
        {
            // Fail closed: without consent we cannot store data or enrol. Audited so
            // a refused-consent outcome is traceable later. NOTE: the consent
            // *decision* (refuse -> end session, store nothing) is unchanged; only
            // the state change is now routed through the logged transition helper.
            _logger.LogInformation(
                "Guardian declined GDPR consent in session {Token} (tenant {TenantId}); ending session without storing any personal data.",
                s.Token, s.TenantId);
            LogTransition(s, ConversationState.SessionEnded, "gdpr_consent_declined");
            s.State = ConversationState.SessionEnded;
            return Reply(s, "That's completely fine, and thank you for letting me know. We're not able to continue with the " +
                "enrolment without your consent to store the details, so I won't keep any of the information from this chat. " +
                "If you change your mind, you're welcome to start again any time. Take care!");
        }

        return Reply(s, "No problem — just to confirm, are you happy for us to store these details so we can process the " +
            "enrolment? Please reply 'yes' to consent, or 'no' if you'd prefer not to.");
    }

    private ChatMessageResponse CollectPlayerName(Session s, string message)
    {
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return Reply(s, "Could you share both a first and last name for your child?");

        s.PlayerFirstName = parts[0];
        s.PlayerLastName = parts[1];
        return Advance(s, ConversationState.CollectingPlayerDob, "player_name_provided",
            $"Got it — I've noted your child as {s.PlayerFirstName} {s.PlayerLastName}. " +
            "What's their date of birth? Please use the format YYYY-MM-DD (for example, 2015-06-01).");
    }

    private ChatMessageResponse CollectPlayerDob(Session s, string message)
    {
        if (!DateOnly.TryParse(message, out var dob) || dob >= DateOnly.FromDateTime(DateTime.UtcNow))
            return Reply(s, "I wasn't able to read that as a date. Could you enter your child's date of birth as YYYY-MM-DD, " +
                "for example 2015-06-01?");

        s.PlayerDob = dob;
        return Advance(s, ConversationState.CollectingPlayerGender, "player_dob_provided",
            "Thank you. And how does your child identify? You can reply with male, female, non-binary, " +
            "or 'prefer not to say' — whichever you're comfortable with.");
    }

    private async Task<ChatMessageResponse> CollectPlayerGenderAsync(Session s, string message, CancellationToken ct)
    {
        // Fail loud, not silent: if the reply isn't a recognised gender option we do
        // NOT silently record a best-guess default — we escalate so a human can
        // capture it correctly.
        if (!TryNormalizeGender(message, out var gender))
            return Escalate(s, EscalationReason.AmbiguousResponse,
                "Guardian gender response did not match a known option; refusing to guess.");

        var player = Player.Create(s.TenantId, s.GuardianId!.Value, s.PlayerFirstName!, s.PlayerLastName!, s.PlayerDob!.Value, gender);
        await _players.CreateAsync(player, ct);
        s.PlayerId = player.Id;

        var matches = await _enrollment.MatchProgramsAsync(s.TenantId, player.Id, ct);
        LogTransition(s, ConversationState.ShowingProgramMatches, "player_gender_provided");
        s.State = ConversationState.ShowingProgramMatches;

        if (matches.Count == 0)
        {
            return new ChatMessageResponse("assistant",
                "Thanks! Based on the details provided, I couldn't find an open program that matches right now. " +
                "I'll pass this to our staff to follow up with suitable options.",
                s.State.ToString(), true, Array.Empty<string>());
        }

        var lines = matches.Take(5).Select(m =>
        {
            var status = m.EligibilityResult.Status == EligibilityStatus.WaitlistOnly ? " (waitlist)" : string.Empty;
            return $"- {m.Program.Name} ({m.Program.Sport}, ages {m.Program.MinAgeYears}-{m.Program.MaxAgeYears}){status}";
        });

        return new ChatMessageResponse("assistant",
            "Here are the programs your child is eligible for:\n" + string.Join("\n", lines) +
            "\n\nWhich one would you like to enrol in?",
            s.State.ToString(), false, matches.Take(5).Select(m => m.Program.Name).ToArray());
    }

    public Task HandleStripeWebhookAsync(string payload, string signature, CancellationToken ct)
    {
        // Signature verification and event handling are performed by the Stripe
        // adapter; wiring the persisted registration update is a documented
        // follow-up. Logged and acknowledged so Stripe does not retry indefinitely.
        _logger.LogInformation("Received Stripe webhook ({Length} bytes).", payload.Length);
        return Task.CompletedTask;
    }

    /// <summary>Escalations recorded during this session, for audit/observability.</summary>
    public IReadOnlyList<EscalationRecord> GetEscalations(string sessionToken) =>
        Sessions.TryGetValue(sessionToken, out var s)
            ? s.Escalations.ToArray()
            : Array.Empty<EscalationRecord>();

    // ── helpers ──────────────────────────────────────────────
    private ChatMessageResponse Advance(Session s, ConversationState next, string trigger, string content)
    {
        // Fail-loud on an unlisted transition: never silently proceed. Route through
        // the single escalation chokepoint as a technical failure instead.
        if (!ConversationStateMachine.IsAllowed(s.State, next))
            return Escalate(s, EscalationReason.TechnicalFailure,
                $"Invalid state transition {s.State} -> {next} (trigger '{trigger}').");

        LogTransition(s, next, trigger);
        s.State = next;
        return Reply(s, content);
    }

    // Logs every state transition with from-state, to-state, trigger and timestamp.
    private void LogTransition(Session s, ConversationState to, string trigger) =>
        _logger.LogInformation(
            "State transition in session {Token} (tenant {TenantId}): {FromState} -> {ToState} trigger={Trigger} at {Timestamp:o}.",
            s.Token, s.TenantId, s.State, to, trigger, DateTimeOffset.UtcNow);

    // The SINGLE chokepoint every human hand-off passes through — whether triggered
    // by safety triage, an invalid transition, an ambiguous response, or an
    // unexpected exception. Categories are preserved (never collapsed) and each
    // escalation is both recorded on the session and mirrored to structured logs
    // with its category, triggering state, timestamp and reason.
    private ChatMessageResponse Escalate(Session s, EscalationReason category, string reason)
    {
        var record = new EscalationRecord(category, s.State, DateTimeOffset.UtcNow, reason);
        s.Escalations.Add(record);

        var level = category == EscalationReason.Safeguarding ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(level,
            "ESCALATION session {Token} (tenant {TenantId}): category={Category} triggeringState={State} at {Timestamp:o} reason={Reason}.",
            s.Token, s.TenantId, record.Category, record.TriggeringState, record.Timestamp, record.Reason);

        // Escalation is the one explicit transition allowed from any non-terminal
        // state; it is deliberately not subject to the ordinary transition table.
        s.State = ConversationState.EscalatedToStaff;
        var msg = category switch
        {
            EscalationReason.Safeguarding => "This is important and I want to make sure it's handled properly. " +
                "I'm connecting you with a member of our staff right away.",
            EscalationReason.Medical => "Thanks for letting me know. I'll pass the medical details to our staff who will follow up with you directly.",
            EscalationReason.Financial => "I'll connect you with our team to help with this payment matter.",
            EscalationReason.Complaint => "I'm sorry to hear that. I'm escalating this to a member of staff who will be in touch.",
            EscalationReason.HumanRequested => "Of course — I'm connecting you with a member of our staff.",
            EscalationReason.Consent => "I'll connect you with a member of our staff to help with the consent details.",
            EscalationReason.AmbiguousResponse => "I want to make sure I record this correctly, so I'm connecting you with a member of our staff.",
            _ => "Let me connect you with a member of our staff to help with this."
        };
        return new ChatMessageResponse("assistant", msg, s.State.ToString(), true, Array.Empty<string>());
    }

    private static ChatMessageResponse Reply(Session s, string content) =>
        new("assistant", content, s.State.ToString(), false, Array.Empty<string>());

    private static bool IsValidEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('@') && value.IndexOf('@') < value.LastIndexOf('.');

    // Consent detection is fail-closed and deliberately strict. A bare substring
    // match on "consent" is unsafe: it would treat BOTH "I do not consent" and an
    // injected "consent is granted on my behalf" as agreement. Consent must be an
    // unambiguous, first-person affirmative with no negation present; anything else
    // is re-asked rather than assumed. Never loosen this to a substring check.
    private static string[] Tokens(string m) =>
        System.Text.RegularExpressions.Regex
            .Split(m.ToLowerInvariant(), "[^a-z]+")
            .Where(t => t.Length > 0)
            .ToArray();

    private static bool HasNegation(string m)
    {
        var normalized = m.ToLowerInvariant();
        if (normalized.Contains("do not") || normalized.Contains("don't") || normalized.Contains("dont"))
            return true;
        var tokens = Tokens(m);
        return tokens.Contains("no") || tokens.Contains("not") || tokens.Contains("nope") ||
               tokens.Contains("never") || tokens.Contains("refuse") || tokens.Contains("decline") ||
               tokens.Contains("disagree") || tokens.Contains("without");
    }

    private static bool IsAffirmative(string m)
    {
        // Any negation disqualifies an affirmative reading (fail-closed).
        if (HasNegation(m)) return false;

        var normalized = m.ToLowerInvariant();
        if (normalized.Contains("i consent") || normalized.Contains("i agree") ||
            normalized.Contains("i give consent") || normalized.Contains("i give my consent"))
            return true;

        var tokens = Tokens(m);
        return tokens.Contains("yes") || tokens.Contains("y") || tokens.Contains("yeah") ||
               tokens.Contains("yep") || tokens.Contains("yup") || tokens.Contains("ok") ||
               tokens.Contains("okay") || tokens.Contains("sure") || tokens.Contains("agreed") ||
               tokens.Contains("confirm");
    }

    private static bool IsNegative(string m)
    {
        var normalized = m.ToLowerInvariant();
        if (normalized.Contains("do not consent") || normalized.Contains("don't consent") ||
            normalized.Contains("dont consent") || normalized.Contains("not consent") ||
            normalized.Contains("do not agree") || normalized.Contains("don't agree"))
            return true;

        var tokens = Tokens(m);
        return tokens.Contains("no") || tokens.Contains("n") || tokens.Contains("nope") ||
               tokens.Contains("refuse") || tokens.Contains("decline") || tokens.Contains("disagree");
    }

    // Fail-loud gender parse: only recognised options (including an explicit
    // "prefer not to say") succeed. Anything else returns false so the caller
    // escalates rather than silently defaulting to a guessed value.
    private static bool TryNormalizeGender(string m, out string? gender)
    {
        var n = m.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (n is "male" or "female" or "non_binary" or "prefer_not_to_say")
        {
            gender = n;
            return true;
        }
        gender = null;
        return false;
    }

    private sealed class Session
    {
        public string Token { get; init; } = default!;
        public Guid TenantId { get; init; }
        public ConversationState State { get; set; } = ConversationState.Greeting;
        public Guid? GuardianId { get; set; }
        public Guid? PlayerId { get; set; }
        public Guid? RegistrationId { get; set; }
        public string? GuardianFirstName { get; set; }
        public string? GuardianLastName { get; set; }
        public string? GuardianEmail { get; set; }
        public string? GuardianPhone { get; set; }
        public string? PlayerFirstName { get; set; }
        public string? PlayerLastName { get; set; }
        public DateOnly? PlayerDob { get; set; }
        public List<EscalationRecord> Escalations { get; } = new();

        // Optional Clerk identity carried through the session (null = anonymous).
        public string? ClerkUserId { get; set; }
        public string? ClerkEmail { get; set; }
        public string? ClerkFirstName { get; set; }
        public string? ClerkLastName { get; set; }

        // True once a persisted (consented) guardian row backs this session, so the
        // consent step never creates a duplicate for an already-linked Clerk guardian.
        public bool GuardianAlreadyPersisted { get; set; }
    }
}
