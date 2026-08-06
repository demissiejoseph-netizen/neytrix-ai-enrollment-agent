using Dapper;
using NeytrixAI.Domain.Entities;
using NeytrixAI.Domain.Repositories;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Infrastructure.Data.Repositories;

public sealed class ProgramRepository : IProgramRepository
{
    private const string Projection = """
        id, tenant_id AS TenantId, name, description, sport, min_age_years AS MinAgeYears,
        max_age_years AS MaxAgeYears, gender_policy AS GenderPolicy, skill_level AS SkillLevel,
        capacity, price_cents AS PriceCents, deposit_cents AS DepositCents, currency,
        start_date AS StartDate, end_date AS EndDate, registration_open_at AS RegistrationOpenAt,
        registration_close_at AS RegistrationCloseAt, location, stripe_price_id AS StripePriceId,
        is_active AS IsActive, created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ProgramRepository(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<Program?> GetByIdAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM programs WHERE tenant_id = @TenantId AND id = @ProgramId";
        return await connection.QuerySingleOrDefaultAsync<Program>(new CommandDefinition(sql, new { TenantId = tenantId, ProgramId = programId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Program>> GetByTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"SELECT {Projection} FROM programs WHERE tenant_id = @TenantId ORDER BY name";
        return await connection.QueryAsync<Program>(new CommandDefinition(sql, new { TenantId = tenantId }, cancellationToken: cancellationToken));
    }

    public async Task<IEnumerable<Program>> FindEligibleProgramsAsync(Guid tenantId, int playerAge, string? skillLevel, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        var sql = $"""
            SELECT {Projection}
            FROM programs
            WHERE tenant_id = @TenantId
              AND min_age_years <= @PlayerAge
              AND max_age_years >= @PlayerAge
              AND is_active = TRUE
              AND registration_open_at <= NOW()
              AND (registration_close_at IS NULL OR registration_close_at >= NOW())
              AND (@SkillLevel IS NULL OR skill_level = @SkillLevel OR skill_level = 'all')
            ORDER BY name
            """.Replace("{Projection}", Projection, StringComparison.Ordinal);
        var normalizedSkill = string.IsNullOrWhiteSpace(skillLevel) ? null : skillLevel.Trim().ToLowerInvariant();
        return await connection.QueryAsync<Program>(new CommandDefinition(sql, new { TenantId = tenantId, PlayerAge = playerAge, SkillLevel = normalizedSkill }, cancellationToken: cancellationToken));
    }

    public async Task<Guid> CreateAsync(Program program, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(program.TenantId, cancellationToken);
        const string sql = """
            INSERT INTO programs (id, tenant_id, name, description, sport, min_age_years, max_age_years, gender_policy,
                                  skill_level, capacity, price_cents, deposit_cents, currency, start_date, end_date,
                                  registration_open_at, registration_close_at, location, stripe_price_id, is_active, created_at, updated_at)
            VALUES (@Id, @TenantId, @Name, @Description, @Sport, @MinAgeYears, @MaxAgeYears, @GenderPolicy,
                    @SkillLevel, @Capacity, @PriceCents, @DepositCents, @Currency, @StartDate, @EndDate,
                    @RegistrationOpenAt, @RegistrationCloseAt, @Location, @StripePriceId, @IsActive, @CreatedAt, @UpdatedAt)
            RETURNING id;
            """;
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, program, cancellationToken: cancellationToken));
    }

    public async Task UpdateAsync(Program program, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(program.TenantId, cancellationToken);
        const string sql = """
            UPDATE programs
            SET name = @Name, description = @Description, sport = @Sport, min_age_years = @MinAgeYears,
                max_age_years = @MaxAgeYears, gender_policy = @GenderPolicy, skill_level = @SkillLevel,
                capacity = @Capacity, price_cents = @PriceCents, deposit_cents = @DepositCents, currency = @Currency,
                start_date = @StartDate, end_date = @EndDate, registration_open_at = @RegistrationOpenAt,
                registration_close_at = @RegistrationCloseAt, location = @Location, stripe_price_id = @StripePriceId,
                is_active = @IsActive, updated_at = @UpdatedAt
            WHERE tenant_id = @TenantId AND id = @Id;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, program, cancellationToken: cancellationToken));
    }

    public async Task<bool> HasCapacityAsync(Guid tenantId, Guid programId, CancellationToken cancellationToken = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(tenantId, cancellationToken);
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM programs p
                WHERE p.tenant_id = @TenantId
                  AND p.id = @ProgramId
                  AND p.capacity > (
                      SELECT COUNT(*)
                      FROM registrations r
                      WHERE r.tenant_id = @TenantId
                        AND r.program_id = @ProgramId
                        AND r.status IN ('enrolled', 'pending_payment')
                  )
            );
            """;
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { TenantId = tenantId, ProgramId = programId }, cancellationToken: cancellationToken));
    }
}
