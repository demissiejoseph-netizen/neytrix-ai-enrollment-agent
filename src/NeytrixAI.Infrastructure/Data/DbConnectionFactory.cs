using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NeytrixAI.Infrastructure.Data;

public interface IDbConnectionFactory
{
    Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates PostgreSQL connections with the RLS tenant context set on that connection, backed by
/// a single <see cref="NpgsqlDataSource"/> for the process lifetime. A shared data source (rather
/// than a bare <c>new NpgsqlConnection(...)</c> per call) is required for GAP-04's pgvector
/// support: Pgvector.Npgsql registers the <c>vector</c> type mapping onto an
/// <see cref="NpgsqlDataSourceBuilder"/> via <c>UseVector()</c>, and Npgsql 7+ removed the old
/// process-global type mapper, so that registration only takes effect for connections opened
/// from that specific data source. This class - and its DI registration - must stay a singleton
/// so the vector mapping and Npgsql's physical connection pool are each built exactly once.
/// </summary>
public sealed class DbConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource? _dataSource;

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

        // knowledge_chunks.embedding is a pgvector `vector` column (GAP-04). Pgvector.Vector
        // implements IEnumerable<float>, which Dapper would otherwise try to expand into a
        // comma-separated list of scalar parameters (its normal handling for IN (...) clauses).
        // Registering an explicit handler makes Dapper pass the Vector straight through as a
        // single parameter value instead, matching the DateOnly/jsonb handlers above.
        SqlMapper.AddTypeHandler(typeof(Pgvector.Vector), VectorTypeHandler.Instance);
    }

    public DbConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(_connectionString))
        {
            var builder = new NpgsqlDataSourceBuilder(_connectionString);
            builder.UseVector();
            _dataSource = builder.Build();
        }
    }

    public async Task<IDbConnection> CreateConnectionAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (_dataSource is null)
            throw new InvalidOperationException("DefaultConnection not found in configuration.");

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
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

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
    }
}
