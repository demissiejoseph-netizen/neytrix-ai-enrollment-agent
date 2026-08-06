using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class GuardianRepository : IGuardianRepository
{
    private const string Projection = """
        id, tenant_id AS TenantId, first_name AS FirstName, last_name AS LastName,
        email, phone, preferred_contact AS PreferredContact,
        gdpr_consented_at AS GdprConsentedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public GuardianRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Guardian?> GetByIdAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM guardians WHERE tenant_id = @TenantId AND id = @GuardianId";
        return await connection.QuerySingleOrDefaultAsync<Guardian>(new CommandDefinition(sql, new { TenantId = tenantId, GuardianId = guardianId }, cancellationToken: cancellationToken));
    }

    public async Task<Guardian?> GetByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM guardians WHERE tenant_id = @TenantId AND email = @Email";
        return await connection.QuerySingleOrDefaultAsync<Guardian>(new CommandDefinition(sql, new { TenantId = tenantId, Email = email.Trim().ToLowerInvariant() }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Guardian>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM guardians WHERE tenant_id = @TenantId ORDER BY created_at DESC";
        return await connection.QueryAsync<Guardian>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Guardian guardian, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(guardian.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO guardians (id, tenant_id, first_name, last_name, email, phone, preferred_contact, gdpr_consented_at, created_at, updated_at)
            VALUES (@Id, @TenantId, @FirstName, @LastName, @Email, @Phone, @PreferredContact, @GdprConsentedAt, @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, guardian, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Guardian guardian, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(guardian.TenantId, cancellationToken);
        const string sql = """
            UPDATE guardians
            SET first_name = @FirstName, last_name = @LastName, email = @Email, phone = @Phone,
                preferred_contact = @PreferredContact, gdpr_consented_at = @GdprConsentedAt, updated_at = @UpdatedAt
            WHERE tenant_id = @TenantId AND id = @Id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, guardian, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM guardians WHERE tenant_id = @TenantId AND id = @GuardianId);";
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { TenantId = tenantId, GuardianId = guardianId }, cancellationToken: cancellationToken));
    }
}
