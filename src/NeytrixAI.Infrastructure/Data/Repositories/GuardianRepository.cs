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
        clerk_user_id AS ClerkUserId,
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

    public async Task<Guardian?> GetByClerkUserIdAsync(Guid tenantId, string clerkUserId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM guardians WHERE clerk_user_id = @ClerkUserId";
        return await conn.QuerySingleOrDefaultAsync<Guardian>(
            new CommandDefinition(sql, new { ClerkUserId = clerkUserId.Trim() }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Guardian>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM guardians ORDER BY created_at DESC";
        return await conn.QueryAsync<Guardian>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Guardian guardian, CancellationToken cancellationToken = default)
    {
        // Fail-closed GDPR gate at the write path itself (not just the API layer):
        // a guardian's personal data must never be persisted without recorded
        // consent. This throws BEFORE any connection is opened, so no data leaves
        // the process. Callers that legitimately create a guardian must have called
        // RecordGdprConsent() first.
        if (guardian.GdprConsentedAt is null)
            throw new InvalidOperationException(
                "Cannot store guardian: GDPR consent has not been recorded. Storage is blocked until consent is given.");

        using var conn = await _connectionFactory.CreateConnectionAsync(guardian.TenantId, cancellationToken);
        const string sql = @"
            INSERT INTO guardians (id, tenant_id, first_name, last_name, email, phone, preferred_contact, gdpr_consented_at, clerk_user_id, created_at, updated_at)
            VALUES (@Id, @TenantId, @FirstName, @LastName, @Email, @Phone, @PreferredContact, @GdprConsentedAt, @ClerkUserId, @CreatedAt, @UpdatedAt)
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
                gdpr_consented_at = @GdprConsentedAt, clerk_user_id = @ClerkUserId,
                updated_at = @UpdatedAt
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
