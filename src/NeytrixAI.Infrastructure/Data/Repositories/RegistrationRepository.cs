using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using System.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class RegistrationRepository : IRegistrationRepository
{
    private readonly DbConnectionFactory _connectionFactory;

    public RegistrationRepository(DbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Registration?> GetByIdAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, player_id AS PlayerId, program_id AS ProgramId, 
                   status, payment_status AS PaymentStatus, stripe_payment_intent_id AS StripePaymentIntentId, 
                   calendar_event_id AS CalendarEventId, registration_date AS RegistrationDate, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM registrations
            WHERE id = @Id";
        return await conn.QueryFirstOrDefaultAsync<Registration>(sql, new { Id = id });
    }

    public async Task<IEnumerable<Registration>> GetByPlayerIdAsync(Guid playerId, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, player_id AS PlayerId, program_id AS ProgramId, 
                   status, payment_status AS PaymentStatus, stripe_payment_intent_id AS StripePaymentIntentId, 
                   calendar_event_id AS CalendarEventId, registration_date AS RegistrationDate, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM registrations
            WHERE player_id = @PlayerId
            ORDER BY created_at DESC";
        return await conn.QueryAsync<Registration>(sql, new { PlayerId = playerId });
    }

    public async Task<IEnumerable<Registration>> GetByProgramIdAsync(Guid programId, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, player_id AS PlayerId, program_id AS ProgramId, 
                   status, payment_status AS PaymentStatus, stripe_payment_intent_id AS StripePaymentIntentId, 
                   calendar_event_id AS CalendarEventId, registration_date AS RegistrationDate, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM registrations
            WHERE program_id = @ProgramId
            ORDER BY created_at DESC";
        return await conn.QueryAsync<Registration>(sql, new { ProgramId = programId });
    }

    public async Task<IEnumerable<Registration>> GetAllAsync(Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = @"
            SELECT id, tenant_id AS TenantId, player_id AS PlayerId, program_id AS ProgramId, 
                   status, payment_status AS PaymentStatus, stripe_payment_intent_id AS StripePaymentIntentId, 
                   calendar_event_id AS CalendarEventId, registration_date AS RegistrationDate, 
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM registrations
            ORDER BY created_at DESC";
        return await conn.QueryAsync<Registration>(sql);
    }

    public async Task<Registration> CreateAsync(Registration registration)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(registration.TenantId);
        var sql = @"
            INSERT INTO registrations (id, tenant_id, player_id, program_id, status, payment_status, 
                                       stripe_payment_intent_id, calendar_event_id, registration_date, 
                                       created_at, updated_at)
            VALUES (@Id, @TenantId, @PlayerId, @ProgramId, @Status, @PaymentStatus, 
                    @StripePaymentIntentId, @CalendarEventId, @RegistrationDate, 
                    @CreatedAt, @UpdatedAt)
            RETURNING id, tenant_id AS TenantId, player_id AS PlayerId, program_id AS ProgramId, 
                      status, payment_status AS PaymentStatus, stripe_payment_intent_id AS StripePaymentIntentId, 
                      calendar_event_id AS CalendarEventId, registration_date AS RegistrationDate, 
                      created_at AS CreatedAt, updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Registration>(sql, registration);
    }

    public async Task<Registration> UpdateAsync(Registration registration)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(registration.TenantId);
        var sql = @"
            UPDATE registrations
            SET status = @Status, payment_status = @PaymentStatus, 
                stripe_payment_intent_id = @StripePaymentIntentId, 
                calendar_event_id = @CalendarEventId, updated_at = @UpdatedAt
            WHERE id = @Id
            RETURNING id, tenant_id AS TenantId, player_id AS PlayerId, program_id AS ProgramId, 
                      status, payment_status AS PaymentStatus, stripe_payment_intent_id AS StripePaymentIntentId, 
                      calendar_event_id AS CalendarEventId, registration_date AS RegistrationDate, 
                      created_at AS CreatedAt, updated_at AS UpdatedAt";
        return await conn.QuerySingleAsync<Registration>(sql, registration);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid tenantId)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId);
        var sql = "DELETE FROM registrations WHERE id = @Id";
        var rowsAffected = await conn.ExecuteAsync(sql, new { Id = id });
        return rowsAffected > 0;
    }
}
