using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NeytrixAI.Api.Controllers;
using NeytrixAI.Api.Tools;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Domain.Services;
using NeytrixAI.Infrastructure.Adapters;
using NeytrixAI.Infrastructure.Services;
using Stripe.Checkout;

namespace NeytrixAI.Api.Services;

public sealed class AgentOrchestrationService : IAgentOrchestrationService
{
    private static readonly string[] ToolDeclarations =
    [
        "answer_faq", "upsert_guardian", "add_player", "match_programs",
        "get_available_slots", "book_assessment", "send_waiver", "create_payment_link",
        "create_registration", "escalate_to_staff", "check_registration_status"
    ];

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConversationRepository _conversations;
    private readonly IGuardianRepository _guardians;
    private readonly IProgramRepository _programs;
    private readonly IRegistrationRepository _registrations;
    private readonly IAgentModelClient _modelClient;
    private readonly IStripeAdapter _stripeAdapter;
    private readonly StripeOptions _stripeOptions;

    public AgentOrchestrationService(
        IHttpContextAccessor httpContextAccessor,
        IConversationRepository conversations,
        IGuardianRepository guardians,
        IProgramRepository programs,
        IRegistrationRepository registrations,
        IAgentModelClient modelClient,
        IStripeAdapter stripeAdapter,
        IOptions<StripeOptions> stripeOptions)
    {
        _httpContextAccessor = httpContextAccessor;
        _conversations = conversations;
        _guardians = guardians;
        _programs = programs;
        _registrations = registrations;
        _modelClient = modelClient;
        _stripeAdapter = stripeAdapter;
        _stripeOptions = stripeOptions.Value;
    }

    public async Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken ct)
    {
        var tenantId = GetRequestTenantId();
        Guid? guardianId = null;
        if (!string.IsNullOrWhiteSpace(request.GuardianEmail))
            guardianId = (await _guardians.GetByEmailAsync(tenantId, request.GuardianEmail, ct))?.Id;

        var now = DateTimeOffset.UtcNow;
        var token = CreateSessionToken();
        var session = new ConversationSession(
            Guid.NewGuid(), tenantId, guardianId, token,
            string.IsNullOrWhiteSpace(request.Channel) ? "widget" : request.Channel,
            ConversationState.Greeting.ToString(), "{}", null, now, now);
        await _conversations.CreateSessionAsync(session, ct);

        const string greeting = "Hello! I can help with programs, registration, waivers, and payments. How can I help today?";
        await AddMessageAsync(session, "assistant", greeting, ct);
        return new StartSessionResponse(token, greeting, session.State);
    }

    public async Task<SessionStateResponse?> GetSessionStateAsync(string sessionToken, CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionToken, ct);
        return session is null ? null : ToStateResponse(session);
    }

    public async Task<ChatMessageResponse?> ProcessMessageAsync(string sessionToken, SendMessageRequest request, CancellationToken ct)
    {
        var session = await GetSessionAsync(sessionToken, ct);
        if (session is null)
            return null;

        await AddMessageAsync(session, "user", request.Content, ct);
        var current = ParseState(session.State) ?? ConversationState.Greeting;
        var modelReply = await _modelClient.GenerateReplyAsync(new AgentModelRequest(sessionToken, current.ToString(), request.Content, ToolDeclarations), ct);
        var requested = ParseState(modelReply.RequestedState) ?? current;
        var transition = requested == current
            ? new StateTransitionResult(true, current)
            : ConversationStateMachine.Transition(current, requested);
        var newState = transition.IsValid ? transition.NewState : current;
        var updated = session with { State = newState.ToString(), EndedAt = ConversationStateMachine.IsTerminal(newState) ? DateTimeOffset.UtcNow : null, UpdatedAt = DateTimeOffset.UtcNow };
        await _conversations.UpdateSessionAsync(updated, ct);
        await AddMessageAsync(updated, "assistant", modelReply.Content, ct);

        return new ChatMessageResponse(
            "assistant",
            modelReply.Content,
            newState.ToString(),
            ConversationStateMachine.RequiresEscalation(newState),
            modelReply.SuggestedActions?.ToArray() ?? Array.Empty<string>());
    }

    public async Task HandleStripeWebhookAsync(string payload, string signature, CancellationToken ct)
    {
        var stripeEvent = _stripeAdapter.ParseWebhookEvent(payload, signature, _stripeOptions.WebhookSecret);
        if (!string.Equals(stripeEvent.Type, "checkout.session.completed", StringComparison.Ordinal))
            return;

        if (stripeEvent.Data.Object is not Session checkout ||
            !checkout.Metadata.TryGetValue("registration_id", out var registrationIdValue) ||
            !Guid.TryParse(registrationIdValue, out var registrationId))
            return;

        // Stripe's existing metadata only contains registration_id. This lookup relies on the
        // webhook database role being permitted to resolve the registration before its tenant is known.
        var registration = await _registrations.GetByIdAsync(Guid.Empty, registrationId, ct);
        if (registration is null)
            return;

        var program = await _programs.GetByIdAsync(registration.TenantId, registration.ProgramId, ct);
        if (program is null)
            return;

        registration.RecordPayment(checkout.PaymentIntentId ?? checkout.Id, checkout.AmountTotal ?? 0L, program.PriceCents);
        await _registrations.UpdateAsync(registration, ct);
    }

    private async Task<ConversationSession?> GetSessionAsync(string sessionToken, CancellationToken ct) =>
        await _conversations.GetByTokenAsync(GetRequestTenantId(), sessionToken, ct);

    private Guid GetRequestTenantId()
    {
        if (_httpContextAccessor.HttpContext?.Items["TenantId"] is Guid tenantId)
            return tenantId;
        throw new InvalidOperationException("No tenant was resolved for this request.");
    }

    private static ConversationState? ParseState(string? value) =>
        Enum.TryParse<ConversationState>(value, ignoreCase: true, out var state) ? state : null;

    private static SessionStateResponse ToStateResponse(ConversationSession session)
    {
        var state = ParseState(session.State) ?? ConversationState.Greeting;
        return new SessionStateResponse(session.SessionToken, state.ToString(), ConversationStateMachine.IsTerminal(state), session.GuardianId, null, null);
    }

    private Task AddMessageAsync(ConversationSession session, string role, string content, CancellationToken ct) =>
        _conversations.AddMessageAsync(new ConversationMessage(Guid.NewGuid(), session.Id, session.TenantId, role, content, null, null, null, null, DateTimeOffset.UtcNow), ct);

    private static string CreateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
