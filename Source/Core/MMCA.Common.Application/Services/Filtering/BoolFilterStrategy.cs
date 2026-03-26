using System.Collections.Frozen;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="bool"/> and <see cref="Nullable{Boolean}"/> properties.
/// Supports only the "IS" operator. Silently returns the unfiltered query if the value
/// cannot be parsed as a boolean, preventing runtime exceptions from invalid input.
/// </summary>
internal sealed class BoolFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "IS"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
    {
        if (!bool.TryParse(value, out var boolValue))
            return query;

        return op switch
        {
            "IS" => query.Where($"{property} == @0", boolValue),
            _ => query
        };
    }
}
