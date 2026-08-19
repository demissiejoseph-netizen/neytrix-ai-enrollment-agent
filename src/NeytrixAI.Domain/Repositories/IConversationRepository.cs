namespace NeytrixAI.Domain.Repositories;

/// <summary>Persistence contract for durable widget/email conversation state and messages.</summary>
public interface IConversationRepository
{
    Task<ConversationSession?> GetByTokenAsync(Guid tenantId, string sessionToken, CancellationToken cancellationToken = default);
    Task<Guid> CreateSessionAsync(ConversationSession session, CancellationToken cancellationToken = default);
    Task UpdateSessionAsync(ConversationSession session, CancellationToken cancellationToken = default);
    Task<Guid> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default);

    /// <summary>Loads the full message history for a session in chronological order (oldest first).</summary>
    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record ConversationSession(
    Guid Id,
    Guid TenantId,
    Guid? GuardianId,
    string SessionToken,
    string Channel,
    string State,
    string ContextJson,
    DateTimeOffset? EndedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ConversationMessage(
    Guid Id,
    Guid SessionId,
    Guid TenantId,
    string Role,
    string Content,
    string? ToolName,
    string? ToolArgsJson,
    string? ToolResultJson,
    int? TokensUsed,
    DateTimeOffset CreatedAt);
