using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>
/// Drives the real Gemini function-calling loop for a single incoming user message: load
/// history, ask the model for its next turn, execute any function call (dispatching set_stage
/// to the state machine directly and everything else to <see cref="IToolExecutionService"/>),
/// feed the result back as a function response, and repeat until the model produces a final
/// reply or the iteration cap is hit. Hitting the cap without a final reply is treated as a
/// fail-closed condition and forces an escalation, matching the project's guardrail that a
/// broken agent must degrade to "get a human," never hang or bluff.
/// </summary>
public sealed class AgentOrchestrationService : IAgentOrchestrationService
{
    private const int MaxLoopIterations = 8;

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConversationRepository _conversations;
    private readonly IGuardianRepository _guardians;
    private readonly IProgramRepository _programs;
    private readonly IRegistrationRepository _registrations;
    private readonly IAgentModelClient _modelClient;
    private readonly IToolExecutionService _toolExecution;
    private readonly IStripeAdapter _stripeAdapter;
    private readonly StripeOptions _stripeOptions;

    public AgentOrchestrationService(
        IHttpContextAccessor httpContextAccessor,
        IConversationRepository conversations,
        IGuardianRepository guardians,
        IProgramRepository programs,
        IRegistrationRepository registrations,
        IAgentModelClient modelClient,
        IToolExecutionService toolExecution,
        IStripeAdapter stripeAdapter,
        IOptions<StripeOptions> stripeOptions)
    {
        _httpContextAccessor = httpContextAccessor;
        _conversations = conversations;
        _guardians = guardians;
        _programs = programs;
        _registrations = registrations;
        _modelClient = modelClient;
        _toolExecution = toolExecution;
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

        var current = ParseState(session.State) ?? ConversationState.Greeting;

        if (ConversationStateMachine.IsTerminal(current))
        {
            const string endedMessage = "This conversation has ended. Please start a new session if you need further help.";
            await AddMessageAsync(session, "assistant", endedMessage, ct);
            return new ChatMessageResponse("assistant", endedMessage, current.ToString(), ConversationStateMachine.RequiresEscalation(current), Array.Empty<string>());
        }

        await AddMessageAsync(session, "user", request.Content, ct);

        for (var iteration = 0; iteration < MaxLoopIterations; iteration++)
        {
            var history = await BuildHistoryAsync(session, ct);
            var allowedNext = ConversationStateMachine.AllowedTransitions(current).Select(s => s.ToString()).ToArray();
            var turn = await _modelClient.GetNextTurnAsync(
                new AgentModelRequest(session.SessionToken, current.ToString(), allowedNext, history, GeminiToolSchemas.All),
                ct);

            if (turn.IsFunctionCall && turn.FunctionCallName is not null)
            {
                string responseJson;

                if (turn.FunctionCallName == GeminiToolSchemas.SetStageFunctionName)
                {
                    var (applied, error, newState) = TryApplySetStage(current, turn.FunctionCallArgsJson);
                    if (applied)
                    {
                        current = newState;
                        session = await PersistStateAsync(session, current, ct);
                    }

                    responseJson = applied
                        ? JsonSerializer.Serialize(new { applied = true, current_state = current.ToString() }, ToolJsonOptions.Model)
                        : JsonSerializer.Serialize(new { applied = false, error }, ToolJsonOptions.Model);
                }
                else
                {
                    var result = await _toolExecution.ExecuteAsync(session.TenantId, session, turn.FunctionCallName, turn.FunctionCallArgsJson ?? "{}", ct);
                    if (result.UpdatedSession is not null)
                        session = result.UpdatedSession;
                    responseJson = result.ResultJson;
                }

                await AddMessageAsync(session, "assistant", string.Empty, ct, toolName: turn.FunctionCallName, toolArgsJson: turn.FunctionCallArgsJson);
                await AddMessageAsync(session, "tool", string.Empty, ct, toolName: turn.FunctionCallName, toolResultJson: responseJson);
                continue;
            }

            // Final text turn. RequestedState is only meaningful here for simpler clients
            // (e.g. the fail-safe fallback) that never call set_stage - the real Vertex loop
            // changes state exclusively through set_stage function calls handled above.
            if (turn.RequestedState is not null)
            {
                var requested = ParseState(turn.RequestedState) ?? current;
                if (requested != current)
                {
                    var transition = ConversationStateMachine.Transition(current, requested);
                    if (transition.IsValid)
                    {
                        current = transition.NewState;
                        session = await PersistStateAsync(session, current, ct);
                    }
                }
            }

            var finalText = string.IsNullOrWhiteSpace(turn.FinalText)
                ? "I'm not sure how to respond to that. A staff member can help."
                : turn.FinalText;
            await AddMessageAsync(session, "assistant", finalText, ct);

            return new ChatMessageResponse(
                "assistant", finalText, current.ToString(),
                ConversationStateMachine.RequiresEscalation(current), Array.Empty<string>());
        }

        // Loop exhausted without a final reply - fail closed rather than hang or bluff.
        var escalation = ConversationStateMachine.Transition(current, ConversationState.EscalatedToStaff);
        if (escalation.IsValid)
        {
            current = escalation.NewState;
            session = await PersistStateAsync(session, current, ct);
        }

        const string fallback = "I'm having trouble completing this right now. A staff member will follow up with you.";
        await AddMessageAsync(session, "assistant", fallback, ct);
        return new ChatMessageResponse("assistant", fallback, current.ToString(), ConversationStateMachine.RequiresEscalation(current), Array.Empty<string>());
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

    // ── history construction ──────────────────────────────────────
    private async Task<IReadOnlyList<ModelHistoryTurn>> BuildHistoryAsync(ConversationSession session, CancellationToken ct)
    {
        var messages = await _conversations.GetMessagesAsync(session.TenantId, session.Id, ct);
        return messages.Select(ToHistoryTurn).ToList();
    }

    private static ModelHistoryTurn ToHistoryTurn(ConversationMessage message) => message.Role switch
    {
        "user" => new ModelHistoryTurn(ModelTurnRole.User, Text: message.Content),
        "assistant" when message.ToolName is not null =>
            new ModelHistoryTurn(ModelTurnRole.Model, FunctionCallName: message.ToolName, FunctionCallArgsJson: message.ToolArgsJson),
        "assistant" => new ModelHistoryTurn(ModelTurnRole.Model, Text: message.Content),
        "tool" => new ModelHistoryTurn(ModelTurnRole.Function, FunctionResponseName: message.ToolName, FunctionResponseJson: message.ToolResultJson),
        _ => new ModelHistoryTurn(ModelTurnRole.User, Text: message.Content)
    };

    // ── set_stage control function (never touches IToolExecutionService) ──
    private static (bool Applied, string? Error, ConversationState NewState) TryApplySetStage(ConversationState current, string? argsJson)
    {
        string? nextStateName;
        try
        {
            var node = string.IsNullOrWhiteSpace(argsJson) ? null : JsonNode.Parse(argsJson) as JsonObject;
            nextStateName = node?["next_state"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return (false, "next_state arguments were not valid JSON.", current);
        }

        if (string.IsNullOrWhiteSpace(nextStateName))
            return (false, "next_state is required.", current);

        if (!Enum.TryParse<ConversationState>(nextStateName, ignoreCase: true, out var requested))
            return (false, $"'{nextStateName}' is not a recognized conversation stage.", current);

        var transition = ConversationStateMachine.Transition(current, requested);
        return transition.IsValid ? (true, null, transition.NewState) : (false, transition.ErrorMessage, current);
    }

    private async Task<ConversationSession> PersistStateAsync(ConversationSession session, ConversationState newState, CancellationToken ct)
    {
        var updated = session with
        {
            State = newState.ToString(),
            EndedAt = ConversationStateMachine.IsTerminal(newState) ? DateTimeOffset.UtcNow : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _conversations.UpdateSessionAsync(updated, ct);
        return updated;
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
        var context = ParseContext(session.ContextJson);
        var playerId = TryGetGuid(context, "player_id");
        var registrationId = TryGetGuid(context, "registration_id");
        var guardianId = session.GuardianId ?? TryGetGuid(context, "guardian_id");
        return new SessionStateResponse(session.SessionToken, state.ToString(), ConversationStateMachine.IsTerminal(state), guardianId, playerId, registrationId);
    }

    private static JsonObject ParseContext(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(contextJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static Guid? TryGetGuid(JsonObject context, string key) =>
        context.TryGetPropertyValue(key, out var node) &&
        node is JsonValue value &&
        value.TryGetValue(out string? text) &&
        Guid.TryParse(text, out var guid)
            ? guid
            : null;

    private Task AddMessageAsync(
        ConversationSession session, string role, string content, CancellationToken ct,
        string? toolName = null, string? toolArgsJson = null, string? toolResultJson = null) =>
        _conversations.AddMessageAsync(
            new ConversationMessage(Guid.NewGuid(), session.Id, session.TenantId, role, content, toolName, toolArgsJson, toolResultJson, null, DateTimeOffset.UtcNow),
            ct);

    private static string CreateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
