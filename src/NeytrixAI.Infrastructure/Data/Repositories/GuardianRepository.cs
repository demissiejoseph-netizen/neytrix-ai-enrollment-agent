using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using System.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class GuardianRepository : IGuardianRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public GuardianRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Guardian?> GetByIdAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, email, first_name AS FirstName, 
                   last_name AS LastName, phone, created_at AS CreatedAt, 
                   updated_at AS UpdatedAt
            FROM guardians
            WHERE id = @Id";
        return await conn.QueryFirstOrDefaultAsync<Guardian>(sql, new { Id = id });
    }

    public async Task<Guardian?> GetByEmailAsync(string email, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, email, first_name AS FirstName, 
                   last_name AS LastName, phone, created_at AS CreatedAt, 
                   updated_at AS UpdatedAt
            FROM guardians
            WHERE email = @Email";
        return await conn.QueryFirstOrDefaultAsync<Guardian>(sql, new { Email = email });
    }

    public async Task<IEnumerable<Guardian>> GetAllAsync(Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, email, first_name AS FirstName, 
                   last_name AS LastName, phone, created_at AS CreatedAt, 
                   updated_at AS UpdatedAt
            FROM guardians
            ORDER BY created_at DESC";
        return await conn.QueryAsync<Guardian>(sql);
    }

    public async Task<Guardian> CreateAsync(Guardian guardian)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(guardian.TenantId);
        var sql = @"
            INSERT INTO guardians (id, tenant_id, email, first_name, last_name, phone, created_at, updated_at)
            VALUES (@Id, @TenantId, @Email, @FirstName, @LastName, @Phone, @CreatedAt, @UpdatedAt)
            RETURNING id, tenant_id AS TenantId, email, first_name AS FirstName, 
                      last_name AS LastName, phone, created_at AS CreatedAt, updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Guardian>(sql, guardian);
    }

    public async Task<Guardian> UpdateAsync(Guardian guardian)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(guardian.TenantId);
        var sql = @"
            UPDATE guardians
            SET email = @Email, first_name = @FirstName, last_name = @LastName, 
                phone = @Phone, updated_at = @UpdatedAt
            WHERE id = @Id
            RETURNING id, tenant_id AS TenantId, email, first_name AS FirstName, 
                      last_name AS LastName, phone, created_at AS CreatedAt, updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Guardian>(sql, guardian);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = "DELETE FROM guardians WHERE id = @Id";
        var rowsAffected = await conn.ExecuteAsync(sql, new { Id = id });
        return rowsAffected > 0;
    }
}
