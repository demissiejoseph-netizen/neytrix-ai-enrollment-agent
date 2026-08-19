using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class AuditLogRepository : IAuditLogRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AuditLogRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Guid> AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(entry.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO audit_log (id, tenant_id, actor_type, actor_id, action, resource_type, resource_id, payload, created_at)
            VALUES (@Id, @TenantId, @ActorType, @ActorId, @Action, @ResourceType, @ResourceId, CAST(@PayloadJson AS jsonb), @CreatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, entry, cancellationToken: cancellationToken));
    }
}
