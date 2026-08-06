using Dapper;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class ConversationRepository : IConversationRepository
{
    private const string SessionProjection = """
        id, tenant_id AS TenantId, guardian_id AS GuardianId, session_token AS SessionToken,
        channel, state, context::text AS ContextJson, ended_at AS EndedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ConversationRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<ConversationSession?> GetByTokenAsync(Guid tenantId, string sessionToken, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {SessionProjection} FROM conversation_sessions WHERE tenant_id = @TenantId AND session_token = @SessionToken";
        return await connection.QuerySingleOrDefaultAsync<ConversationSession>(new CommandDefinition(sql, new { TenantId = tenantId, SessionToken = sessionToken }, cancellationToken: cancellationToken));
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
}
