namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// Safe local fallback used when Vertex AI is not configured (e.g. VertexAI:ProjectId missing).
/// Deliberately does not attempt to run a real conversation - it always ends the turn by
/// requesting escalation to staff, matching the project's fail-closed design: a misconfigured
/// deploy must degrade to "get a human", never silently pretend to be a working agent.
/// </summary>
public sealed class NullAgentModelClient : IAgentModelClient
{
    public Task<AgentModelTurn> GetNextTurnAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentModelTurn(
            IsFunctionCall: false,
            FinalText: "I'm unable to access the enrollment assistant right now. A staff member can help you continue.",
            RequestedState: "EscalatedToStaff"));
}
