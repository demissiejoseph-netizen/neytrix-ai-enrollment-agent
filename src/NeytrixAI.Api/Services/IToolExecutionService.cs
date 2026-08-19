using NeytrixAI.Domain.Repositories;

namespace NeytrixAI.Api.Services;

/// <summary>
/// Result of executing one named tool. Business-rule failures (e.g. "guardian has not
/// consented", "program not found") are represented as Success=false with a JSON error
/// payload in ResultJson - they are fed back to the model as a function response so it
/// can adjust, not thrown as exceptions. Only genuinely unexpected failures propagate.
/// </summary>
public sealed record ToolExecutionResult(bool Success, string ResultJson, ConversationSession? UpdatedSession = null);

public interface IToolExecutionService
{
    Task<ToolExecutionResult> ExecuteAsync(
        Guid tenantId,
        ConversationSession session,
        string toolName,
        string argsJson,
        CancellationToken cancellationToken);
}
