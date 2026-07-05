using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class TenantRepository : ITenantRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public TenantRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        
        const string sql = @"
            SELECT id, slug, name, settings, created_at, updated_at
            FROM tenants
            WHERE id = @TenantId";

        return await connection.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        // For slug lookup, we can't use RLS since we don't have tenantId yet
        // Use a connection without tenant context
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        
        const string sql = @"
            SELECT id, slug, name, settings, created_at, updated_at
            FROM tenants
            WHERE slug = @Slug";

        return await connection.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { Slug = slug }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        
        const string sql = @"
            SELECT id, slug, name, settings, created_at, updated_at
            FROM tenants
            ORDER BY name";

        return await connection.QueryAsync<Tenant>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        
        const string sql = @"
            INSERT INTO tenants (slug, name, settings)
            VALUES (@Slug, @Name, @Settings::jsonb)
            RETURNING id";

        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(sql, tenant, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenant.Id, cancellationToken);
        
        const string sql = @"
            UPDATE tenants
            SET name = @Name,
                settings = @Settings::jsonb
            WHERE id = @Id";

        await connection.ExecuteAsync(
            new CommandDefinition(sql, tenant, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        
        const string sql = "SELECT EXISTS(SELECT 1 FROM tenants WHERE id = @TenantId)";

        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }
}
