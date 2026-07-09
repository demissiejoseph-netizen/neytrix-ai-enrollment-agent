using System.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NeytrixAI.Infrastructure.Data;

public interface IDbConnectionFactory
{
    Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("DefaultConnection not found in configuration");
    }

    public async Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Set the RLS tenant context for THIS connection. This is the sole
        // cross-tenant isolation enforcement point: every query on this connection
        // is filtered by RLS policies against 'app.tenant_id'. The variable name
        // MUST match current_setting('app.tenant_id') used by the policies in
        // db/migrations/001_initial_schema.sql. set_config is used (not SET)
        // because SET does not accept bound parameters.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.tenant_id', @tenantId, false)";
        cmd.Parameters.Add(new NpgsqlParameter("tenantId", tenantId.ToString()));
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
