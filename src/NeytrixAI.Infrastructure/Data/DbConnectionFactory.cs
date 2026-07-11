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
        var raw = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection not found in configuration");

        // Fail fast instead of hanging when the database is unreachable. Explicit
        // connect/command timeouts and a bounded pool keep a struggling DB from
        // exhausting request threads. Values already present in configuration win.
        var builder = new NpgsqlConnectionStringBuilder(raw);
        if (builder.Timeout == 15) builder.Timeout = 10;                 // 15 = Npgsql default
        if (builder.CommandTimeout == 30) builder.CommandTimeout = 15;   // 30 = Npgsql default
        if (!builder.ContainsKey("Maximum Pool Size")) builder.MaxPoolSize = 50;
        _connectionString = builder.ConnectionString;
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
