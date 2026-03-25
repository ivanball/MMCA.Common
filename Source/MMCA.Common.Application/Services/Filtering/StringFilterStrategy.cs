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
        "STARTS WITH", "ENDS WITH", "IS EMPTY", "IS NOT EMPTY"
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
            "IS EMPTY" => query.Where($"string.IsNullOrEmpty({property})"),
            "IS NOT EMPTY" => query.Where($"!string.IsNullOrEmpty({property})"),
            _ => query
        };
}
