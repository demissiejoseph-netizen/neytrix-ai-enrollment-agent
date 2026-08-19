using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class AssessmentRepository : IAssessmentRepository
{
    private const string Projection = """
        id, tenant_id AS TenantId, registration_id AS RegistrationId, google_event_id AS GoogleEventId,
        scheduled_at AS ScheduledAt, duration_minutes AS DurationMinutes, location, notes,
        outcome, assessed_at AS AssessedAt, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public AssessmentRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Assessment?> GetByIdAsync(Guid tenantId, Guid assessmentId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM assessments WHERE tenant_id = @TenantId AND id = @AssessmentId";
        return await connection.QuerySingleOrDefaultAsync<Assessment>(new CommandDefinition(sql, new { TenantId = tenantId, AssessmentId = assessmentId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Assessment>> GetByRegistrationAsync(Guid tenantId, Guid registrationId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM assessments WHERE tenant_id = @TenantId AND registration_id = @RegistrationId ORDER BY created_at DESC";
        return await connection.QueryAsync<Assessment>(new CommandDefinition(sql, new { TenantId = tenantId, RegistrationId = registrationId }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Assessment assessment, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(assessment.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO assessments (id, tenant_id, registration_id, google_event_id, scheduled_at,
                                      duration_minutes, location, notes, outcome, assessed_at, created_at, updated_at)
            VALUES (@Id, @TenantId, @RegistrationId, @GoogleEventId, @ScheduledAt,
                    @DurationMinutes, @Location, @Notes, @Outcome, @AssessedAt, @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, assessment, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Assessment assessment, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(assessment.TenantId, cancellationToken);
        const string sql = """
            UPDATE assessments
            SET google_event_id = @GoogleEventId, scheduled_at = @ScheduledAt, duration_minutes = @DurationMinutes,
                location = @Location, notes = @Notes, outcome = @Outcome, assessed_at = @AssessedAt, updated_at = @UpdatedAt
            WHERE tenant_id = @TenantId AND id = @Id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, assessment, cancellationToken: cancellationToken));
    }
}
