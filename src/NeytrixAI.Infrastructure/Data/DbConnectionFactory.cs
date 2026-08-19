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

        // Dapper 2.1.35 has no built-in DateOnly support in any of its shipped TFM builds (it
        // throws NotSupportedException the moment a DateOnly value is bound as a query
        // parameter), even though the referenced entities (Program.StartDate/EndDate,
        // Player.DateOfBirth) use DateOnly. Without this handler, ProgramRepository and
        // PlayerRepository CreateAsync/UpdateAsync fail on every real call. Registered once,
        // globally, since Dapper's type handler cache is process-wide.
        SqlMapper.AddTypeHandler(typeof(DateOnly), DateOnlyTypeHandler.Instance);
        SqlMapper.AddTypeHandler(typeof(DateOnly?), DateOnlyTypeHandler.Instance);

        // Tenant.Settings is a Dictionary<string, object> backed by a jsonb column; Npgsql
        // returns jsonb as a plain string without dynamic-JSON support enabled, and Dapper's
        // default row mapper can't convert that string to a Dictionary on its own.
        SqlMapper.AddTypeHandler(typeof(Dictionary<string, object>), JsonDictionaryTypeHandler.Instance);
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
