using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NeytrixAI.Infrastructure.Data;

namespace NeytrixAI.Api.Services;

public sealed class PostgresReadinessHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory _connectionFactory;

    public PostgresReadinessHealthCheck(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync(Guid.Empty, cancellationToken);
            await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.", exception);
        }
    }
}
