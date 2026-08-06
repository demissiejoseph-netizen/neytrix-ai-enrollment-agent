namespace NeytrixAI.Infrastructure.Services;

public interface IAgentModelClient
{
    Task<AgentModelResponse> GenerateReplyAsync(AgentModelRequest request, CancellationToken cancellationToken);
}

public sealed record AgentModelRequest(
    string SessionToken,
    string CurrentState,
    string UserMessage,
    IReadOnlyList<string> ToolDeclarations);

public sealed record AgentModelResponse(
    string Content,
    string? RequestedState = null,
    IReadOnlyList<string>? SuggestedActions = null);
