using System.Text.Json;
using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class TenantRepository : ITenantRepository
{
    private const string Projection = """
        id, slug, name, timezone, stripe_account_id AS StripeAccountId,
        google_calendar_id AS GoogleCalendarId, settings, is_active AS IsActive,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public TenantRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM tenants WHERE id = @TenantId";
        return await connection.QuerySingleOrDefaultAsync<Tenant>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<Tenant?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        // Tenant resolution precedes tenant-scoped RLS requests, so no tenant setting is available yet.
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        var sql = $"SELECT {Projection} FROM tenants WHERE slug = @Slug";
        return await connection.QuerySingleOrDefaultAsync<Tenant>(new CommandDefinition(sql, new { Slug = slug.Trim().ToLowerInvariant() }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        var sql = $"SELECT {Projection} FROM tenants ORDER BY name";
        return await connection.QueryAsync<Tenant>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
        const string sql = """
            INSERT INTO tenants (id, slug, name, timezone, stripe_account_id, google_calendar_id, settings, is_active, created_at, updated_at)
            VALUES (@Id, @Slug, @Name, @Timezone, @StripeAccountId, @GoogleCalendarId, CAST(@SettingsJson AS jsonb), @IsActive, @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, new
        {
            tenant.Id, tenant.Slug, tenant.Name, tenant.Timezone, tenant.StripeAccountId, tenant.GoogleCalendarId,
            SettingsJson = JsonSerializer.Serialize(tenant.Settings), tenant.IsActive, tenant.CreatedAt, tenant.UpdatedAt
        }, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenant.Id, cancellationToken);
        const string sql = """
            UPDATE tenants
            SET slug = @Slug, name = @Name, timezone = @Timezone, stripe_account_id = @StripeAccountId,
                google_calendar_id = @GoogleCalendarId, settings = CAST(@SettingsJson AS jsonb),
                is_active = @IsActive, updated_at = @UpdatedAt
            WHERE id = @Id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            tenant.Id, tenant.Slug, tenant.Name, tenant.Timezone, tenant.StripeAccountId, tenant.GoogleCalendarId,
            SettingsJson = JsonSerializer.Serialize(tenant.Settings), tenant.IsActive, tenant.UpdatedAt
        }, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM tenants WHERE id = @TenantId);";
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Validates and sets the RLS tenant setting on this temporary connection. Repository calls open
    /// their own connections and set <c>app.tenant_id</c> there, which is the per-connection scope
    /// that actually enforces the migration's RLS policies.
    /// </summary>
    public async Task SetTenantSessionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.tenant_id', @TenantId, false);",
            new { TenantId = tenantId.ToString() },
            cancellationToken: cancellationToken));
    }
}
