using System.Data;
using Dapper;

namespace NeytrixAI.Infrastructure.Data;

/// <summary>
/// Dapper 2.1.35 ships with no DateOnly support in any of its target-framework builds (net461,
/// net5.0, net7.0, netstandard2.0) - binding a DateOnly-typed value as a query parameter throws
/// <c>NotSupportedException</c> at runtime, and reading one back from a DATE column relies on
/// Npgsql's own conversion, which is unaffected. This handler covers the parameter-binding gap
/// so <c>Program.StartDate</c>/<c>EndDate</c> and <c>Player.DateOfBirth</c> can actually be
/// inserted/updated through the repositories. Registered once, globally, in
/// <see cref="DbConnectionFactory"/>'s static constructor for both <see cref="DateOnly"/> and
/// <see cref="Nullable{DateOnly}"/>.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public static readonly DateOnlyTypeHandler Instance = new();

    private DateOnlyTypeHandler() { }

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => throw new DataException($"Cannot convert value of type {value.GetType()} to DateOnly.")
    };
}
