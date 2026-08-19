using System.Data;
using Dapper;
using Pgvector;

namespace NeytrixAI.Infrastructure.Data;

/// <summary>
/// Dapper type handler for <see cref="Vector"/> (GAP-04's pgvector <c>vector(1536)</c> column on
/// <c>knowledge_chunks</c>). Two problems this solves:
///
///  - <see cref="Vector"/> implements <c>IEnumerable&lt;float&gt;</c>, which Dapper would
///    otherwise treat as a list parameter and expand into N separate scalar placeholders (its
///    normal handling for <c>WHERE x IN @list</c>). Explicitly registering a handler makes
///    Dapper bind it as a single parameter value instead.
///  - This handler deliberately does not set <see cref="IDbDataParameter.DbType"/>. Npgsql
///    resolves the Postgres <c>vector</c> type from the parameter Value's CLR type via the
///    mapping Pgvector.Npgsql registers on the <c>NpgsqlDataSourceBuilder</c> in
///    <see cref="DbConnectionFactory"/> (<c>UseVector()</c>); forcing a generic ADO.NET
///    <see cref="DbType"/> here would short-circuit that resolution.
///
/// Registered once, globally, in <see cref="DbConnectionFactory"/>'s static constructor.
/// </summary>
public sealed class VectorTypeHandler : SqlMapper.TypeHandler<Vector>
{
    public static readonly VectorTypeHandler Instance = new();

    private VectorTypeHandler() { }

    public override void SetValue(IDbDataParameter parameter, Vector? value)
    {
        parameter.Value = value is null ? DBNull.Value : value;
    }

    public override Vector Parse(object value) => value switch
    {
        Vector v => v,
        float[] arr => new Vector(arr),
        _ => throw new DataException($"Cannot convert value of type {value.GetType()} to Vector.")
    };
}
