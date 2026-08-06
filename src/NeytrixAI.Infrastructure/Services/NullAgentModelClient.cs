namespace NeytrixAI.Infrastructure.Services;

/// <summary>Safe local fallback used when Vertex AI is not configured.</summary>
public sealed class NullAgentModelClient : IAgentModelClient
{
    public Task<AgentModelResponse> GenerateReplyAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentModelResponse(
            "I’m unable to access the enrollment assistant right now. A staff member can help you continue.",
            "EscalatedToStaff",
            ["escalate_to_staff"]));
}
