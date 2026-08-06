using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class RegistrationRepository : IRegistrationRepository
{
    private const string Projection = """
        id, tenant_id AS TenantId, guardian_id AS GuardianId, player_id AS PlayerId, program_id AS ProgramId,
        status, waitlist_position AS WaitlistPosition, stripe_payment_intent_id AS StripePaymentIntentId,
        stripe_checkout_session_id AS StripeCheckoutSessionId, amount_paid_cents AS AmountPaidCents,
        waiver_sent_at AS WaiverSentAt, waiver_signed_at AS WaiverSignedAt, enrolled_at AS EnrolledAt,
        notes, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public RegistrationRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Registration?> GetByIdAsync(Guid tenantId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM registrations WHERE tenant_id = @TenantId AND id = @RegistrationId";
        return await connection.QuerySingleOrDefaultAsync<Registration>(new CommandDefinition(sql, new { TenantId = tenantId, RegistrationId = registrationId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Registration>> GetBySessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        // registrations has no session_id; sessions are related through their guardian_id.
        const string sql = """
            SELECT r.id, r.tenant_id AS TenantId, r.guardian_id AS GuardianId, r.player_id AS PlayerId, r.program_id AS ProgramId,
                   r.status, r.waitlist_position AS WaitlistPosition, r.stripe_payment_intent_id AS StripePaymentIntentId,
                   r.stripe_checkout_session_id AS StripeCheckoutSessionId, r.amount_paid_cents AS AmountPaidCents,
                   r.waiver_sent_at AS WaiverSentAt, r.waiver_signed_at AS WaiverSignedAt, r.enrolled_at AS EnrolledAt,
                   r.notes, r.created_at AS CreatedAt, r.updated_at AS UpdatedAt
            FROM registrations r
            INNER JOIN conversation_sessions s
                ON s.guardian_id = r.guardian_id AND s.tenant_id = r.tenant_id
            WHERE r.tenant_id = @TenantId AND s.tenant_id = @TenantId AND s.id = @SessionId
            ORDER BY r.created_at DESC;
            """;
        return await connection.QueryAsync<Registration>(new CommandDefinition(sql, new { TenantId = tenantId, SessionId = sessionId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Registration>> GetByPlayerAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM registrations WHERE tenant_id = @TenantId AND player_id = @PlayerId ORDER BY created_at DESC";
        return await connection.QueryAsync<Registration>(new CommandDefinition(sql, new { TenantId = tenantId, PlayerId = playerId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Registration>> GetByProgramAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM registrations WHERE tenant_id = @TenantId AND program_id = @ProgramId ORDER BY created_at DESC";
        return await connection.QueryAsync<Registration>(new CommandDefinition(sql, new { TenantId = tenantId, ProgramId = programId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Registration registration, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(registration.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO registrations (id, tenant_id, guardian_id, player_id, program_id, status, waitlist_position,
                                       stripe_payment_intent_id, stripe_checkout_session_id, amount_paid_cents,
                                       waiver_sent_at, waiver_signed_at, enrolled_at, notes, created_at, updated_at)
            VALUES (@Id, @TenantId, @GuardianId, @PlayerId, @ProgramId, @Status, @WaitlistPosition,
                    @StripePaymentIntentId, @StripeCheckoutSessionId, @AmountPaidCents,
                    @WaiverSentAt, @WaiverSignedAt, @EnrolledAt, @Notes, @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, registration, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Registration registration, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(registration.TenantId, cancellationToken);
        const string sql = """
            UPDATE registrations
            SET guardian_id = @GuardianId, player_id = @PlayerId, program_id = @ProgramId, status = @Status,
                waitlist_position = @WaitlistPosition, stripe_payment_intent_id = @StripePaymentIntentId,
                stripe_checkout_session_id = @StripeCheckoutSessionId, amount_paid_cents = @AmountPaidCents,
                waiver_sent_at = @WaiverSentAt, waiver_signed_at = @WaiverSignedAt, enrolled_at = @EnrolledAt,
                notes = @Notes, updated_at = @UpdatedAt
            WHERE tenant_id = @TenantId AND id = @Id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, registration, cancellationToken: cancellationToken));
    }

    public async Task UpdateStatusAsync(Guid tenantId, Guid registrationId, string status, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = "UPDATE registrations SET status = @Status, updated_at = NOW() WHERE tenant_id = @TenantId AND id = @RegistrationId;";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { TenantId = tenantId, RegistrationId = registrationId, Status = status }, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, Guid playerId, Guid programId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM registrations WHERE tenant_id = @TenantId AND player_id = @PlayerId AND program_id = @ProgramId);";
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { TenantId = tenantId, PlayerId = playerId, ProgramId = programId }, cancellationToken: cancellationToken));
    }
}
