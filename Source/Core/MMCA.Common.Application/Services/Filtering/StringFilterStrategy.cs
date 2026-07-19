using System.Collections.Frozen;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="string"/> properties. Supports text-specific operators
/// like CONTAINS, STARTS WITH, ENDS WITH, and IS EMPTY in addition to equality checks.
/// Also used for nested property paths (e.g. "Category.Name") regardless of the target type,
/// since LINQ Dynamic evaluates the full path as a string expression.
/// </summary>
internal sealed class StringFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CONTAINS", "NOT CONTAINS", "EQUALS", "NOT EQUALS",
        "STARTS WITH", "ENDS WITH", "IS EMPTY", "IS NOT EMPTY", "IN"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "CONTAINS" => query.Where($"{property}.Contains(@0)", value),
            "NOT CONTAINS" => query.Where($"!{property}.Contains(@0)", value),
            "EQUALS" => query.Where($"{property} == @0", value),
            "NOT EQUALS" => query.Where($"{property} != @0", value),
            "STARTS WITH" => query.Where($"{property}.StartsWith(@0)", value),
            "ENDS WITH" => query.Where($"{property}.EndsWith(@0)", value),
            _ => ApplyPresenceOrSet(query, property, op, value),
        };

    // Presence checks (value-independent) and the comma-separated IN set, split out of the main
    // switch to keep each method under the cyclomatic-complexity ceiling.
    private static IQueryable<T> ApplyPresenceOrSet<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IS EMPTY" => query.Where($"string.IsNullOrEmpty({property})"),
            "IS NOT EMPTY" => query.Where($"!string.IsNullOrEmpty({property})"),
            "IN" => ApplyIn(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseStringList(value);
        return values.Count == 0 ? query : query.Where($"@0.Contains({property})", values);
    }
}
