using NeytrixAI.Api.Controllers;

namespace NeytrixAI.Api.Services;

public interface IAgentOrchestrationService
{
    Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken ct);
    Task<SessionStateResponse?> GetSessionStateAsync(string sessionToken, CancellationToken ct);
    Task<ChatMessageResponse?> ProcessMessageAsync(string sessionToken, SendMessageRequest request, CancellationToken ct);
    Task HandleStripeWebhookAsync(string payload, string signature, CancellationToken ct);
}
