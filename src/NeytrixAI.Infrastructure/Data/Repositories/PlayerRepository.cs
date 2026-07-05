using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using System.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class PlayerRepository : IPlayerRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public PlayerRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Player?> GetByIdAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, guardian_id AS GuardianId, 
                   first_name AS FirstName, last_name AS LastName, 
                   date_of_birth AS DateOfBirth, grade_level AS GradeLevel, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM players
            WHERE id = @Id";
        return await conn.QueryFirstOrDefaultAsync<Player>(sql, new { Id = id });
    }

    public async Task<IEnumerable<Player>> GetByGuardianIdAsync(Guid guardianId, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, guardian_id AS GuardianId, 
                   first_name AS FirstName, last_name AS LastName, 
                   date_of_birth AS DateOfBirth, grade_level AS GradeLevel, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM players
            WHERE guardian_id = @GuardianId
            ORDER BY created_at DESC";
        return await conn.QueryAsync<Player>(sql, new { GuardianId = guardianId });
    }

    public async Task<IEnumerable<Player>> GetAllAsync(Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, guardian_id AS GuardianId, 
                   first_name AS FirstName, last_name AS LastName, 
                   date_of_birth AS DateOfBirth, grade_level AS GradeLevel, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM players
            ORDER BY created_at DESC";
        return await conn.QueryAsync<Player>(sql);
    }

    public async Task<Player> CreateAsync(Player player)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(player.TenantId);
        var sql = @"
            INSERT INTO players (id, tenant_id, guardian_id, first_name, last_name, date_of_birth, grade_level, created_at, updated_at)
            VALUES (@Id, @TenantId, @GuardianId, @FirstName, @LastName, @DateOfBirth, @GradeLevel, @CreatedAt, @UpdatedAt)
            RETURNING id, tenant_id AS TenantId, guardian_id AS GuardianId, 
                      first_name AS FirstName, last_name AS LastName, date_of_birth AS DateOfBirth, 
                      grade_level AS GradeLevel, created_at AS CreatedAt, updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Player>(sql, player);
    }

    public async Task<Player> UpdateAsync(Player player)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(player.TenantId);
        var sql = @"
            UPDATE players
            SET first_name = @FirstName, last_name = @LastName, date_of_birth = @DateOfBirth, 
                grade_level = @GradeLevel, updated_at = @UpdatedAt
            WHERE id = @Id
            RETURNING id, tenant_id AS TenantId, guardian_id AS GuardianId, 
                      first_name AS FirstName, last_name AS LastName, date_of_birth AS DateOfBirth, 
                      grade_level AS GradeLevel, created_at AS CreatedAt, updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Player>(sql, player);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = "DELETE FROM players WHERE id = @Id";
        var rowsAffected = await conn.ExecuteAsync(sql, new { Id = id });
        return rowsAffected > 0;
    }
}
