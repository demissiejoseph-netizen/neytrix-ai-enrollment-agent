using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class PlayerRepository : IPlayerRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public PlayerRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private const string Columns = @"
        id, tenant_id AS TenantId, guardian_id AS GuardianId,
        first_name AS FirstName, last_name AS LastName,
        date_of_birth AS DateOfBirth, gender, medical_notes AS MedicalNotes,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Player?> GetByIdAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM players WHERE id = @Id";
        return await conn.QuerySingleOrDefaultAsync<Player>(
            new CommandDefinition(sql, new { Id = playerId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Player>> GetByGuardianAsync(Guid tenantId, Guid guardianId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM players WHERE guardian_id = @GuardianId ORDER BY created_at DESC";
        return await conn.QueryAsync<Player>(
            new CommandDefinition(sql, new { GuardianId = guardianId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Player>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM players ORDER BY created_at DESC";
        return await conn.QueryAsync<Player>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Player player, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(player.TenantId, cancellationToken);
        const string sql = @"
            INSERT INTO players (id, tenant_id, guardian_id, first_name, last_name, date_of_birth, gender, medical_notes, created_at, updated_at)
            VALUES (@Id, @TenantId, @GuardianId, @FirstName, @LastName, @DateOfBirth, @Gender, @MedicalNotes, @CreatedAt, @UpdatedAt)
            RETURNING id";
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, player, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Player player, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(player.TenantId, cancellationToken);
        const string sql = @"
            UPDATE players
            SET first_name = @FirstName, last_name = @LastName, date_of_birth = @DateOfBirth,
                gender = @Gender, medical_notes = @MedicalNotes, updated_at = @UpdatedAt
            WHERE id = @Id";
        await conn.ExecuteAsync(new CommandDefinition(sql, player, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM players WHERE id = @Id)";
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { Id = playerId }, cancellationToken: cancellationToken));
    }
}
