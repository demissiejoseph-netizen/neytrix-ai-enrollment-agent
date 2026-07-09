using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class GuardianRepository : IGuardianRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public GuardianRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private const string Columns = @"
        id, tenant_id AS TenantId, first_name AS FirstName, last_name AS LastName,
        email, phone, preferred_contact AS PreferredContact,
        gdpr_consented_at AS GdprConsentedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Guardian?> GetByIdAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM guardians WHERE id = @Id";
        return await conn.QuerySingleOrDefaultAsync<Guardian>(
            new CommandDefinition(sql, new { Id = guardianId }, cancellationToken: cancellationToken));
    }

    public async Task<Guardian?> GetByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM guardians WHERE email = @Email";
        return await conn.QuerySingleOrDefaultAsync<Guardian>(
            new CommandDefinition(sql, new { Email = email.Trim().ToLowerInvariant() }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Guardian>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM guardians ORDER BY created_at DESC";
        return await conn.QueryAsync<Guardian>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Guardian guardian, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(guardian.TenantId, cancellationToken);
        const string sql = @"
            INSERT INTO guardians (id, tenant_id, first_name, last_name, email, phone, preferred_contact, gdpr_consented_at, created_at, updated_at)
            VALUES (@Id, @TenantId, @FirstName, @LastName, @Email, @Phone, @PreferredContact, @GdprConsentedAt, @CreatedAt, @UpdatedAt)
            RETURNING id";
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, guardian, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Guardian guardian, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(guardian.TenantId, cancellationToken);
        const string sql = @"
            UPDATE guardians
            SET first_name = @FirstName, last_name = @LastName, email = @Email,
                phone = @Phone, preferred_contact = @PreferredContact,
                gdpr_consented_at = @GdprConsentedAt, updated_at = @UpdatedAt
            WHERE id = @Id";
        await conn.ExecuteAsync(new CommandDefinition(sql, guardian, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM guardians WHERE id = @Id)";
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { Id = guardianId }, cancellationToken: cancellationToken));
    }
}
