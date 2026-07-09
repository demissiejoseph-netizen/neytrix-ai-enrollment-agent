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

    private const string Columns = @"
        id, slug, name, timezone,
        stripe_account_id AS StripeAccountId,
        google_calendar_id AS GoogleCalendarId,
        is_active AS IsActive,
        created_at AS CreatedAt,
        updated_at AS UpdatedAt";

    public async Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM tenants WHERE id = @TenantId";
        return await connection.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        // Tenant resolution happens before a tenant context is known. The tenants
        // table exposes a SELECT-only RLS policy for this bootstrap lookup.
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        var sql = $"SELECT {Columns} FROM tenants WHERE slug = @Slug";
        return await connection.QuerySingleOrDefaultAsync<Tenant>(
            new CommandDefinition(sql, new { Slug = slug }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        var sql = $"SELECT {Columns} FROM tenants ORDER BY name";
        return await connection.QueryAsync<Tenant>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        const string sql = @"
            INSERT INTO tenants (id, slug, name, timezone, stripe_account_id, google_calendar_id, is_active, created_at, updated_at)
            VALUES (@Id, @Slug, @Name, @Timezone, @StripeAccountId, @GoogleCalendarId, @IsActive, @CreatedAt, @UpdatedAt)
            RETURNING id";
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(sql, tenant, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenant.Id, cancellationToken);
        const string sql = @"
            UPDATE tenants
            SET name = @Name, timezone = @Timezone,
                stripe_account_id = @StripeAccountId,
                google_calendar_id = @GoogleCalendarId,
                is_active = @IsActive, updated_at = @UpdatedAt
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
