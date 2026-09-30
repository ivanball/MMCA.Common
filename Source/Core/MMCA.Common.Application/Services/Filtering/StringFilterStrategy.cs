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

    /// <inheritdoc />
    /// <remarks>
    /// Only IN has a value shape to reject: a list with no usable item would otherwise fail open and
    /// return the unfiltered set, where every value-typed strategy refuses the same input. Every other
    /// string operator accepts any value, including the empty string for EQUALS.
    /// </remarks>
    public bool CanParseValue(string op, string value) =>
        !string.Equals(op, "IN", StringComparison.Ordinal) || FilterValueParser.ParseStringList(value).Count > 0;

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "CONTAINS" => query.Where(DynamicQueryConfig.Parameterized, $"{property}.Contains(@0)", value),
            "NOT CONTAINS" => query.Where(DynamicQueryConfig.Parameterized, $"!{property}.Contains(@0)", value),
            "EQUALS" => query.Where(DynamicQueryConfig.Parameterized, $"{property} == @0", value),
            "NOT EQUALS" => query.Where(DynamicQueryConfig.Parameterized, $"{property} != @0", value),
            "STARTS WITH" => query.Where(DynamicQueryConfig.Parameterized, $"{property}.StartsWith(@0)", value),
            "ENDS WITH" => query.Where(DynamicQueryConfig.Parameterized, $"{property}.EndsWith(@0)", value),
            _ => ApplyPresenceOrSet(query, property, op, value),
        };

    // Presence checks (value-independent) and the comma-separated IN set, split out of the main
    // switch to keep each method under the cyclomatic-complexity ceiling.
    private static IQueryable<T> ApplyPresenceOrSet<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IS EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"string.IsNullOrEmpty({property})"),
            "IS NOT EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"!string.IsNullOrEmpty({property})"),
            "IN" => ApplyIn(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseStringList(value);
        return values.Count == 0 ? query : query.Where(DynamicQueryConfig.Parameterized, $"@0.Contains({property})", values);
    }
}
