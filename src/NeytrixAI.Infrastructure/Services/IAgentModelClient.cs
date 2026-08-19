namespace NeytrixAI.Infrastructure.Services;

/// <summary>
/// One line of conversation history, expressed in Gemini's role vocabulary so both the
/// Vertex client and the deterministic fallback share the same shape. The orchestrator is
/// responsible for translating persisted ConversationMessage rows (role: user/assistant/tool)
/// into this shape and back.
/// </summary>
public enum ModelTurnRole { User, Model, Function }

public sealed record ModelHistoryTurn(
    ModelTurnRole Role,
    string? Text = null,
    string? FunctionCallName = null,
    string? FunctionCallArgsJson = null,
    string? FunctionResponseName = null,
    string? FunctionResponseJson = null);

/// <summary>A tool the model may call, described with a plain JSON Schema (snake_case property names).</summary>
public sealed record ModelToolDeclaration(string Name, string Description, string JsonSchema);

public sealed record AgentModelRequest(
    string SessionToken,
    string CurrentState,
    IReadOnlyList<string> AllowedNextStates,
    IReadOnlyList<ModelHistoryTurn> History,
    IReadOnlyList<ModelToolDeclaration> Tools);

/// <summary>
/// A single model turn: either a function call the orchestrator must execute and feed back,
/// or a final assistant reply. <see cref="RequestedState"/> is only meaningful on a final
/// (non-function-call) turn and is an alternative, simpler path to a state change for clients
/// that do not participate in the full set_stage control-function loop (e.g. the fail-safe
/// fallback client). The orchestrator always re-validates any requested state against
/// ConversationStateMachine before applying it - the model proposes, the graph disposes.
/// </summary>
public sealed record AgentModelTurn(
    bool IsFunctionCall,
    string? FunctionCallName = null,
    string? FunctionCallArgsJson = null,
    string? FinalText = null,
    string? RequestedState = null);

public interface IAgentModelClient
{
    Task<AgentModelTurn> GetNextTurnAsync(AgentModelRequest request, CancellationToken cancellationToken);
}
