using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using System.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class ProgramRepository : IProgramRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public ProgramRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Program?> GetByIdAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, name, description, sport_type AS SportType, 
                   min_age AS MinAge, max_age AS MaxAge, min_grade AS MinGrade, 
                   max_grade AS MaxGrade, capacity, price, schedule, 
                   location, is_active AS IsActive, created_at AS CreatedAt, 
                   updated_at AS UpdatedAt
            FROM programs
            WHERE id = @Id";
        return await conn.QueryFirstOrDefaultAsync<Program>(sql, new { Id = id });
    }

    public async Task<IEnumerable<Program>> GetActiveAsync(Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, name, description, sport_type AS SportType, 
                   min_age AS MinAge, max_age AS MaxAge, min_grade AS MinGrade, 
                   max_grade AS MaxGrade, capacity, price, schedule, 
                   location, is_active AS IsActive, created_at AS CreatedAt, 
                   updated_at AS UpdatedAt
            FROM programs
            WHERE is_active = true
            ORDER BY name";
        return await conn.QueryAsync<Program>(sql);
    }

    public async Task<IEnumerable<Program>> GetAllAsync(Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, name, description, sport_type AS SportType, 
                   min_age AS MinAge, max_age AS MaxAge, min_grade AS MinGrade, 
                   max_grade AS MaxGrade, capacity, price, schedule, 
                   location, is_active AS IsActive, created_at AS CreatedAt, 
                   updated_at AS UpdatedAt
            FROM programs
            ORDER BY name";
        return await conn.QueryAsync<Program>(sql);
    }

    public async Task<Program> CreateAsync(Program program)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(program.TenantId);
        var sql = @"
            INSERT INTO programs (id, tenant_id, name, description, sport_type, min_age, max_age, 
                                  min_grade, max_grade, capacity, price, schedule, location, 
                                  is_active, created_at, updated_at)
            VALUES (@Id, @TenantId, @Name, @Description, @SportType, @MinAge, @MaxAge, 
                    @MinGrade, @MaxGrade, @Capacity, @Price, @Schedule, @Location, 
                    @IsActive, @CreatedAt, @UpdatedAt)
            RETURNING id, tenant_id AS TenantId, name, description, sport_type AS SportType, 
                      min_age AS MinAge, max_age AS MaxAge, min_grade AS MinGrade, 
                      max_grade AS MaxGrade, capacity, price, schedule, 
                      location, is_active AS IsActive, created_at AS CreatedAt, 
                      updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Program>(sql, program);
    }

    public async Task<Program> UpdateAsync(Program program)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(program.TenantId);
        var sql = @"
            UPDATE programs
            SET name = @Name, description = @Description, sport_type = @SportType, 
                min_age = @MinAge, max_age = @MaxAge, min_grade = @MinGrade, 
                max_grade = @MaxGrade, capacity = @Capacity, price = @Price, 
                schedule = @Schedule, location = @Location, is_active = @IsActive, 
                updated_at = @UpdatedAt
            WHERE id = @Id
            RETURNING id, tenant_id AS TenantId, name, description, sport_type AS SportType, 
                      min_age AS MinAge, max_age AS MaxAge, min_grade AS MinGrade, 
                      max_grade AS MaxGrade, capacity, price, schedule, 
                      location, is_active AS IsActive, created_at AS CreatedAt, 
                      updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Program>(sql, program);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = "DELETE FROM programs WHERE id = @Id";
        var rowsAffected = await conn.ExecuteAsync(sql, new { Id = id });
        return rowsAffected > 0;
    }
}
