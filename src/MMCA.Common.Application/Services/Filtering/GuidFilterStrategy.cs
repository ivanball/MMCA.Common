using System.Collections.Frozen;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="Guid"/> and <see cref="Nullable{Guid}"/> properties.
/// Supports equality operators. Silently returns the unfiltered query if the value
/// cannot be parsed as a GUID.
/// </summary>
internal sealed class GuidFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
    {
        if (!Guid.TryParse(value, out var guidValue))
            return query;

        return op switch
        {
            "EQUALS" => query.Where($"{property} == @0", guidValue),
            "NOT EQUALS" => query.Where($"{property} != @0", guidValue),
            _ => query
        };
    }
}
