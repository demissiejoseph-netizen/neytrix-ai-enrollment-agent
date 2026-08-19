using System.Data;
using System.Text.Json;
using Dapper;

namespace NeytrixAI.Infrastructure.Data;

/// <summary>
/// Handles Dapper (de)serialization for <c>Dictionary&lt;string, object&gt;</c>-typed entity
/// properties backed by a Postgres <c>jsonb</c> column (currently just <c>Tenant.Settings</c>).
/// Npgsql returns jsonb columns as plain CLR strings unless dynamic JSON support is explicitly
/// enabled on the data source, so Dapper's reflection-based row mapper tries to convert that
/// string straight to a Dictionary via <c>IConvertible</c> and throws
/// <c>InvalidCastException</c>. This handler parses the JSON text into a dictionary on read, and
/// serializes back to a jsonb-castable JSON string on write (defensive - the repositories
/// currently always pre-serialize this value into a separate string parameter themselves, so the
/// write path here isn't exercised by existing call sites, but is implemented correctly in case
/// that changes). Registered once, globally, in <see cref="DbConnectionFactory"/>'s static
/// constructor.
/// </summary>
public sealed class JsonDictionaryTypeHandler : SqlMapper.TypeHandler<Dictionary<string, object>>
{
    public static readonly JsonDictionaryTypeHandler Instance = new();

    private JsonDictionaryTypeHandler() { }

    public override void SetValue(IDbDataParameter parameter, Dictionary<string, object>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = JsonSerializer.Serialize(value ?? new Dictionary<string, object>());
    }

    public override Dictionary<string, object> Parse(object value) => value switch
    {
        string s when string.IsNullOrWhiteSpace(s) => new Dictionary<string, object>(),
        string s => JsonSerializer.Deserialize<Dictionary<string, object>>(s) ?? new Dictionary<string, object>(),
        Dictionary<string, object> d => d,
        _ => throw new DataException($"Cannot convert value of type {value.GetType()} to Dictionary<string, object>.")
    };
}
