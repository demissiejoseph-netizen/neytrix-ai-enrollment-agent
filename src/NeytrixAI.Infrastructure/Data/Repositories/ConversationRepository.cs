using Dapper;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;
using System.Linq;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class ConversationRepository : IConversationRepository
{
    private const string SessionProjection = """
        id AS Id, tenant_id AS TenantId, guardian_id AS GuardianId, session_token AS SessionToken,
        channel AS Channel, state AS State, context::text AS ContextJson, ended_at AS EndedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ConversationRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    // ConversationSession is a positional record with a Guid?/DateTimeOffset-shaped constructor
    // (kept that way deliberately so AgentOrchestrationService/ToolExecutionService can use
    // non-destructive `with` updates on it). Dapper's default materializer tries to find a
    // constructor whose parameter types exactly match the reader's per-column CLR types (Guid,
    // not Guid?; DateTime, not DateTimeOffset) and throws InvalidOperationException when none
    // matches, rather than falling back to a looser match. So the read path below queries a
    // private DTO row shaped to match Npgsql's actual output types, then maps it onto the
    // domain record by hand - Dapper is never asked to construct a ConversationSession directly.
    private sealed record SessionRow(
        Guid Id, Guid TenantId, Guid? GuardianId, string SessionToken, string Channel, string State,
        string ContextJson, DateTime? EndedAt, DateTime CreatedAt, DateTime UpdatedAt)
    {
        public ConversationSession ToDomain() => new(
            Id, TenantId, GuardianId, SessionToken, Channel, State, ContextJson,
            EndedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(EndedAt.Value, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }

    public async Task<ConversationSession?> GetByTokenAsync(Guid tenantId, string sessionToken, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {SessionProjection} FROM conversation_sessions WHERE tenant_id = @TenantId AND session_token = @SessionToken";
        var row = await connection.QuerySingleOrDefaultAsync<SessionRow>(new CommandDefinition(sql, new { TenantId = tenantId, SessionToken = sessionToken }, cancellationToken: cancellationToken));
        return row?.ToDomain();
    }

    public async Task<Guid> CreateSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(session.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO conversation_sessions (id, tenant_id, guardian_id, session_token, channel, state, context, ended_at, created_at, updated_at)
            VALUES (@Id, @TenantId, @GuardianId, @SessionToken, @Channel, @State, CAST(@ContextJson AS jsonb), @EndedAt, @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, session, cancellationToken: cancellationToken));
    }

    public async Task UpdateSessionAsync(ConversationSession session, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(session.TenantId, cancellationToken);
        const string sql = """
            UPDATE conversation_sessions
            SET guardian_id = @GuardianId, channel = @Channel, state = @State, context = CAST(@ContextJson AS jsonb),
                ended_at = @EndedAt, updated_at = @UpdatedAt
            WHERE tenant_id = @TenantId AND id = @Id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, session, cancellationToken: cancellationToken));
    }

    public async Task<Guid> AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(message.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO conversation_messages (id, session_id, tenant_id, role, content, tool_name, tool_args, tool_result, tokens_used, created_at)
            VALUES (@Id, @SessionId, @TenantId, @Role, @Content, @ToolName, CAST(@ToolArgsJson AS jsonb), CAST(@ToolResultJson AS jsonb), @TokensUsed, @CreatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, message, cancellationToken: cancellationToken));
    }

    // Same Dapper constructor-matching limitation as ConversationSession above - map by hand
    // from a row DTO instead of letting Dapper construct the record directly.
    private sealed record MessageRow(
        Guid Id, Guid SessionId, Guid TenantId, string Role, string Content, string? ToolName,
        string? ToolArgsJson, string? ToolResultJson, int? TokensUsed, DateTime CreatedAt)
    {
        public ConversationMessage ToDomain() => new(
            Id, SessionId, TenantId, Role, Content, ToolName, ToolArgsJson, ToolResultJson, TokensUsed,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)));
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = """
            SELECT id AS Id, session_id AS SessionId, tenant_id AS TenantId, role AS Role, content AS Content, tool_name AS ToolName,
                   tool_args::text AS ToolArgsJson, tool_result::text AS ToolResultJson, tokens_used AS TokensUsed, created_at AS CreatedAt
            FROM conversation_messages
            WHERE tenant_id = @TenantId AND session_id = @SessionId
            ORDER BY created_at ASC, id ASC;
            """;
        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(sql, new { TenantId = tenantId, SessionId = sessionId }, cancellationToken: cancellationToken));
        return rows.Select(r => r.ToDomain()).ToList();
    }
}
