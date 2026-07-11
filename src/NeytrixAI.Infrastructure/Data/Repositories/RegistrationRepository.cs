using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public class RegistrationRepository : IRegistrationRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public RegistrationRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private const string Columns = @"
        id, tenant_id AS TenantId, guardian_id AS GuardianId, player_id AS PlayerId,
        program_id AS ProgramId, status::text AS Status, waitlist_position AS WaitlistPosition,
        stripe_payment_intent_id AS StripePaymentIntentId,
        stripe_checkout_session_id AS StripeCheckoutSessionId,
        amount_paid_cents AS AmountPaidCents,
        waiver_sent_at AS WaiverSentAt, waiver_signed_at AS WaiverSignedAt,
        enrolled_at AS EnrolledAt, notes,
        created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Registration?> GetByIdAsync(Guid tenantId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM registrations WHERE id = @Id";
        return await conn.QuerySingleOrDefaultAsync<Registration>(
            new CommandDefinition(sql, new { Id = registrationId }, cancellationToken: cancellationToken));
    }

    // The current schema has no direct registration<->session linkage; enrollment
    // sessions are tracked separately. Returns empty until such a link exists.
    public Task<IEnumerable<Registration>> GetBySessionAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Registration>>(Array.Empty<Registration>());

    public async Task<IEnumerable<Registration>> GetByPlayerAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM registrations WHERE player_id = @PlayerId ORDER BY created_at DESC";
        return await conn.QueryAsync<Registration>(
            new CommandDefinition(sql, new { PlayerId = playerId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Registration>> GetByProgramAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Columns} FROM registrations WHERE program_id = @ProgramId ORDER BY created_at DESC";
        return await conn.QueryAsync<Registration>(
            new CommandDefinition(sql, new { ProgramId = programId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Registration registration, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(registration.TenantId, cancellationToken);
        const string sql = @"
            INSERT INTO registrations (id, tenant_id, guardian_id, player_id, program_id, status,
                                       waitlist_position, stripe_payment_intent_id, stripe_checkout_session_id,
                                       amount_paid_cents, waiver_sent_at, waiver_signed_at, enrolled_at, notes,
                                       created_at, updated_at)
            VALUES (@Id, @TenantId, @GuardianId, @PlayerId, @ProgramId, @Status::registration_status,
                    @WaitlistPosition, @StripePaymentIntentId, @StripeCheckoutSessionId,
                    @AmountPaidCents, @WaiverSentAt, @WaiverSignedAt, @EnrolledAt, @Notes,
                    @CreatedAt, @UpdatedAt)
            RETURNING id";
        return await conn.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, registration, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Registration registration, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(registration.TenantId, cancellationToken);
        const string sql = @"
            UPDATE registrations
            SET status = @Status::registration_status, waitlist_position = @WaitlistPosition,
                stripe_payment_intent_id = @StripePaymentIntentId,
                stripe_checkout_session_id = @StripeCheckoutSessionId,
                amount_paid_cents = @AmountPaidCents, waiver_sent_at = @WaiverSentAt,
                waiver_signed_at = @WaiverSignedAt, enrolled_at = @EnrolledAt, notes = @Notes,
                updated_at = @UpdatedAt
            WHERE id = @Id";
        await conn.ExecuteAsync(new CommandDefinition(sql, registration, cancellationToken: cancellationToken));
    }

    public async Task UpdateStatusAsync(Guid tenantId, Guid registrationId, string status, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = @"
            UPDATE registrations
            SET status = @Status::registration_status, updated_at = NOW()
            WHERE id = @Id";
        await conn.ExecuteAsync(
            new CommandDefinition(sql, new { Id = registrationId, Status = status }, cancellationToken: cancellationToken));
    }

    public async Task<bool> ExistsAsync(Guid tenantId, Guid playerId, Guid programId, CancellationToken cancellationToken = default)
    {
        using var conn = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = @"
            SELECT EXISTS(
                SELECT 1 FROM registrations
                WHERE player_id = @PlayerId AND program_id = @ProgramId AND status <> 'cancelled')";
        return await conn.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { PlayerId = playerId, ProgramId = programId }, cancellationToken: cancellationToken));
    }
}
