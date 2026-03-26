using System.Collections.Frozen;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="int"/> and <see cref="Nullable{Int32}"/> properties.
/// Supports equality and numeric comparison operators. Silently returns the unfiltered
/// query if the value cannot be parsed as an integer.
/// </summary>
internal sealed class IntFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "GREATER THAN", "LESS THAN",
        "GREATER THAN OR EQUAL", "LESS THAN OR EQUAL"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
    {
        if (!int.TryParse(value, out var intValue))
            return query;

        return op switch
        {
            "EQUALS" => query.Where($"{property} == @0", intValue),
            "NOT EQUALS" => query.Where($"{property} != @0", intValue),
            "GREATER THAN" => query.Where($"{property} > @0", intValue),
            "LESS THAN" => query.Where($"{property} < @0", intValue),
            "GREATER THAN OR EQUAL" => query.Where($"{property} >= @0", intValue),
            "LESS THAN OR EQUAL" => query.Where($"{property} <= @0", intValue),
            _ => query
        };
    }
}
