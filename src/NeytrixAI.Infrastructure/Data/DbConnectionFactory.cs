using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NeytrixAI.Infrastructure.Data;

public interface IDbConnectionFactory
{
    Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Creates a new PostgreSQL connection with the RLS tenant context set on that connection.</summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    static DbConnectionFactory()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public DbConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? string.Empty;
    }

    public async Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
            throw new InvalidOperationException("DefaultConnection not found in configuration.");

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);

            // The migration's RLS policies reference app.tenant_id. set_config is parameterized
            // safely, unlike a PostgreSQL SET statement.
            if (tenantId != Guid.Empty)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT set_config('app.tenant_id', @TenantId, false);",
                    new { TenantId = tenantId.ToString() },
                    cancellationToken: cancellationToken));
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
