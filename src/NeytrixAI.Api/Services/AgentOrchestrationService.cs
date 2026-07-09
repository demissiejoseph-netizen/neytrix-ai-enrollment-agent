using System.Collections.Concurrent;
using NeytrixAI.Api.Controllers;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
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

    public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken ct)
    {
        var tenantId = RequireTenantId();
        var token = Guid.NewGuid().ToString("N");
        var session = new Session { Token = token, TenantId = tenantId, GuardianEmail = request.GuardianEmail };
        Sessions[token] = session;

        const string greeting = "Hi! I can help you enrol your child in one of our programs. " +
            "To get started, what's the parent or guardian's full name?";
        session.State = ConversationState.CollectingGuardianName;

        return Task.FromResult(new StartSessionResponse(token, greeting, session.State.ToString()));
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

        // Fail-closed safety gate: escalate to a human on any safety signal. This
        // runs before any intake parsing so a safeguarding/medical/financial signal
        // can never be swallowed as ordinary form input.
        if (SafetyTriage.ShouldEscalate(request.Content, out var reason))
        {
            _logger.LogWarning(
                "Safety triage matched in session {Token} (tenant {TenantId}, state {State}); escalating with reason {Reason}.",
                s.Token, s.TenantId, s.State, reason);
            return Escalate(s, reason);
        }

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
                _ => Escalate(s, EscalationReason.None)
            };
        }
        catch (Exception ex)
        {
            // Fail closed: never leave the guardian in an undefined automated state.
            _logger.LogError(ex, "Orchestration error in state {State}; escalating.", s.State);
            return Escalate(s, EscalationReason.None);
        }
    }

    private ChatMessageResponse CollectGuardianName(Session s, string message)
    {
        var parts = message.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return Reply(s, "Thanks! Could you share both a first and last name for the parent or guardian?");

        s.GuardianFirstName = parts[0];
        s.GuardianLastName = parts[1];
        return Advance(s, ConversationState.CollectingGuardianEmail,
            $"Lovely to meet you, {s.GuardianFirstName}! What's the best email address to reach you on?");
    }

    private ChatMessageResponse CollectGuardianEmail(Session s, string message)
    {
        if (!IsValidEmail(message))
            return Reply(s, "Hmm, that doesn't look quite like an email address. Could you double-check and type it again?");

        s.GuardianEmail = message.ToLowerInvariant();
        return Advance(s, ConversationState.CollectingGuardianPhone,
            "Great, thank you. And is there a contact phone number we can reach you on? " +
            "If you'd rather not share one, just type 'skip'.");
    }

    private ChatMessageResponse CollectGuardianPhone(Session s, string message)
    {
        s.GuardianPhone = message.Equals("skip", StringComparison.OrdinalIgnoreCase) ? null : message;
        return Advance(s, ConversationState.CollectingGdprConsent,
            "Before we go any further, I need to check one important thing. To process this enrolment we'll need to store " +
            "your details and your child's details, in line with our privacy policy. Are you happy for us to do that? " +
            "Please reply 'yes' to consent, or 'no' if you'd prefer not to.");
    }

    private async Task<ChatMessageResponse> CollectConsentAsync(Session s, string message, CancellationToken ct)
    {
        if (IsAffirmative(message))
        {
            var guardian = Guardian.Create(s.TenantId, s.GuardianFirstName!, s.GuardianLastName!, s.GuardianEmail!, s.GuardianPhone);
            guardian.RecordGdprConsent();
            await _guardians.CreateAsync(guardian, ct);
            s.GuardianId = guardian.Id;
            _logger.LogInformation(
                "Recorded GDPR consent and stored guardian {GuardianId} in session {Token} (tenant {TenantId}).",
                guardian.Id, s.Token, s.TenantId);
            return Advance(s, ConversationState.CollectingPlayerName,
                "Thank you so much. Now let's tell me about your child — what's their full name?");
        }

        if (IsNegative(message))
        {
            // Fail closed: without consent we cannot store data or enrol. Audited so
            // a refused-consent outcome is traceable later.
            _logger.LogInformation(
                "Guardian declined GDPR consent in session {Token} (tenant {TenantId}); ending session without storing any personal data.",
                s.Token, s.TenantId);
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
        return Advance(s, ConversationState.CollectingPlayerDob,
            $"Got it — I've noted your child as {s.PlayerFirstName} {s.PlayerLastName}. " +
            "What's their date of birth? Please use the format YYYY-MM-DD (for example, 2015-06-01).");
    }

    private ChatMessageResponse CollectPlayerDob(Session s, string message)
    {
        if (!DateOnly.TryParse(message, out var dob) || dob >= DateOnly.FromDateTime(DateTime.UtcNow))
            return Reply(s, "I wasn't able to read that as a date. Could you enter your child's date of birth as YYYY-MM-DD, " +
                "for example 2015-06-01?");

        s.PlayerDob = dob;
        return Advance(s, ConversationState.CollectingPlayerGender,
            "Thank you. And how does your child identify? You can reply with male, female, non-binary, " +
            "or 'prefer not to say' — whichever you're comfortable with.");
    }

    private async Task<ChatMessageResponse> CollectPlayerGenderAsync(Session s, string message, CancellationToken ct)
    {
        var gender = NormalizeGender(message);
        var player = Player.Create(s.TenantId, s.GuardianId!.Value, s.PlayerFirstName!, s.PlayerLastName!, s.PlayerDob!.Value, gender);
        await _players.CreateAsync(player, ct);
        s.PlayerId = player.Id;

        var matches = await _enrollment.MatchProgramsAsync(s.TenantId, player.Id, ct);
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

    // ── helpers ──────────────────────────────────────────────
    private ChatMessageResponse Advance(Session s, ConversationState next, string content)
    {
        var result = ConversationStateMachine.Transition(s.State, next);
        if (!result.IsValid)
            return Escalate(s, EscalationReason.None);
        s.State = result.NewState;
        return Reply(s, content);
    }

    private ChatMessageResponse Escalate(Session s, EscalationReason reason)
    {
        // Audit every escalation so incidents (especially safeguarding) are
        // reviewable later. This is the single choke point all escalations pass
        // through, whether triggered by safety triage, an invalid state, or an
        // unexpected exception.
        var level = reason == EscalationReason.Safeguarding ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(level,
            "Escalating session {Token} (tenant {TenantId}) from state {State} to staff. Reason={Reason}.",
            s.Token, s.TenantId, s.State, reason);

        s.State = ConversationState.EscalatedToStaff;
        var msg = reason switch
        {
            EscalationReason.Safeguarding => "This is important and I want to make sure it's handled properly. " +
                "I'm connecting you with a member of our staff right away.",
            EscalationReason.Medical => "Thanks for letting me know. I'll pass the medical details to our staff who will follow up with you directly.",
            EscalationReason.Financial => "I'll connect you with our team to help with this payment matter.",
            EscalationReason.Complaint => "I'm sorry to hear that. I'm escalating this to a member of staff who will be in touch.",
            EscalationReason.HumanRequested => "Of course — I'm connecting you with a member of our staff.",
            _ => "Let me connect you with a member of our staff to help with this."
        };
        return new ChatMessageResponse("assistant", msg, s.State.ToString(), true, Array.Empty<string>());
    }

    private static ChatMessageResponse Reply(Session s, string content) =>
        new("assistant", content, s.State.ToString(), false, Array.Empty<string>());

    private static bool IsValidEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains('@') && value.IndexOf('@') < value.LastIndexOf('.');

    private static bool IsAffirmative(string m) =>
        m.Equals("yes", StringComparison.OrdinalIgnoreCase) || m.Equals("y", StringComparison.OrdinalIgnoreCase) ||
        m.Contains("consent", StringComparison.OrdinalIgnoreCase) || m.Equals("i agree", StringComparison.OrdinalIgnoreCase);

    private static bool IsNegative(string m) =>
        m.Equals("no", StringComparison.OrdinalIgnoreCase) || m.Equals("n", StringComparison.OrdinalIgnoreCase) ||
        m.Contains("do not consent", StringComparison.OrdinalIgnoreCase) || m.Contains("don't consent", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeGender(string m)
    {
        m = m.Trim().ToLowerInvariant().Replace(' ', '_');
        return m is "male" or "female" or "non_binary" or "prefer_not_to_say" ? m : "prefer_not_to_say";
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
    }
}
