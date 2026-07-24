using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="long"/> and <see cref="Nullable{Int64}"/> properties.
/// Supports equality and numeric comparison operators, the comma-separated IN set, an inclusive
/// BETWEEN range, and the IS EMPTY / IS NOT EMPTY null checks. Silently returns the unfiltered
/// query if the value cannot be parsed as a 64-bit integer. Registered by default so long-keyed
/// entities filter without a startup <see cref="QueryFilterService.RegisterStrategy"/> call.
/// </summary>
internal sealed class LongFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "GREATER THAN", "LESS THAN",
        "GREATER THAN OR EQUAL", "LESS THAN OR EQUAL", "IN", "BETWEEN",
        "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool CanParseValue(string op, string value) =>
        FilterValueParser.CanParse(op, value, ParseLong);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "EQUALS" when TryParse(value, out var v) => query.Where($"{property} == @0", v),
            "NOT EQUALS" when TryParse(value, out var v) => query.Where($"{property} != @0", v),
            "GREATER THAN" when TryParse(value, out var v) => query.Where($"{property} > @0", v),
            "LESS THAN" when TryParse(value, out var v) => query.Where($"{property} < @0", v),
            "GREATER THAN OR EQUAL" when TryParse(value, out var v) => query.Where($"{property} >= @0", v),
            "LESS THAN OR EQUAL" when TryParse(value, out var v) => query.Where($"{property} <= @0", v),
            "IS EMPTY" => query.Where($"{property} == null"),
            "IS NOT EMPTY" => query.Where($"{property} != null"),
            // IN/BETWEEN parse a list rather than a single scalar; handle them out of the main switch.
            _ => ApplyInOrRange(query, property, op, value)
        };

    private static bool TryParse(string value, out long result) =>
        long.TryParse(value, CultureInfo.InvariantCulture, out result);

    private static IQueryable<T> ApplyInOrRange<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IN" => ApplyIn(query, property, value),
            "BETWEEN" => ApplyBetween(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseList(value, ParseLong);
        return values.Count == 0 ? query : query.Where($"@0.Contains({property})", values);
    }

    private static IQueryable<T> ApplyBetween<T>(IQueryable<T> query, string property, string value)
    {
        // BETWEEN takes exactly two comma-separated bounds ("min,max"), inclusive on both ends.
        var bounds = FilterValueParser.ParseList(value, ParseLong);
        return bounds.Count == 2
            ? query.Where($"{property} >= @0 && {property} <= @1", bounds[0], bounds[1])
            : query;
    }

    private static long? ParseLong(string s) =>
        long.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
}
