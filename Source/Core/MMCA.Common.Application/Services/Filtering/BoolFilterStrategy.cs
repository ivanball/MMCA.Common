using System.Collections.Frozen;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="bool"/> and <see cref="Nullable{Boolean}"/> properties.
/// Supports the "IS" equality operator plus the "IS EMPTY" / "IS NOT EMPTY" null checks
/// (meaningful for <see cref="Nullable{Boolean}"/> columns). Silently returns the unfiltered
/// query if the value cannot be parsed as a boolean, preventing runtime exceptions from invalid input.
/// </summary>
internal sealed class BoolFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "IS", "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            // Presence checks are value-independent, so they precede the bool parse.
            "IS EMPTY" => query.Where($"{property} == null"),
            "IS NOT EMPTY" => query.Where($"{property} != null"),
            "IS" when bool.TryParse(value, out var boolValue) => query.Where($"{property} == @0", boolValue),
            _ => query
        };
}
