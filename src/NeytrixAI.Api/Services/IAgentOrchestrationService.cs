using NeytrixAI.Api.Controllers;

namespace NeytrixAI.Api.Services;

/// <summary>
/// Drives a guardian-facing enrollment conversation through the deterministic
/// <see cref="NeytrixAI.Domain.Services.ConversationStateMachine"/>. The agent
/// cannot skip required steps (guardian intake, GDPR consent, player intake)
/// and escalates to human staff whenever it is uncertain or detects a
/// safeguarding/medical/complaint signal — it never guesses on safety-relevant
/// input.
/// </summary>
public interface IAgentOrchestrationService
{
    Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken ct);
    Task<SessionStateResponse?> GetSessionStateAsync(string sessionToken, CancellationToken ct);
    Task<ChatMessageResponse?> ProcessMessageAsync(string sessionToken, SendMessageRequest request, CancellationToken ct);
    Task HandleStripeWebhookAsync(string payload, string signature, CancellationToken ct);
}
