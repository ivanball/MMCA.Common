using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="int"/> and <see cref="Nullable{Int32}"/> properties.
/// Supports equality and numeric comparison operators, the comma-separated IN set, an inclusive
/// BETWEEN range, and the IS EMPTY / IS NOT EMPTY null checks. Uses
/// <see cref="CultureInfo.InvariantCulture"/> for parsing, matching the decimal, long and date
/// strategies, so the filter DSL means the same thing under every request culture. Silently returns
/// the unfiltered query if the value cannot be parsed as an integer.
/// </summary>
internal sealed class IntFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "GREATER THAN", "LESS THAN",
        "GREATER THAN OR EQUAL", "LESS THAN OR EQUAL", "IN", "BETWEEN",
        "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool CanParseValue(string op, string value) =>
        FilterValueParser.CanParse(op, value, ParseInt);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "EQUALS" when TryParse(value, out var v) => query.Where(DynamicQueryConfig.Parameterized, $"{property} == @0", v),
            "NOT EQUALS" when TryParse(value, out var v) => query.Where(DynamicQueryConfig.Parameterized, $"{property} != @0", v),
            "GREATER THAN" when TryParse(value, out var v) => query.Where(DynamicQueryConfig.Parameterized, $"{property} > @0", v),
            "LESS THAN" when TryParse(value, out var v) => query.Where(DynamicQueryConfig.Parameterized, $"{property} < @0", v),
            "GREATER THAN OR EQUAL" when TryParse(value, out var v) => query.Where(DynamicQueryConfig.Parameterized, $"{property} >= @0", v),
            "LESS THAN OR EQUAL" when TryParse(value, out var v) => query.Where(DynamicQueryConfig.Parameterized, $"{property} <= @0", v),
            "IS EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"{property} == null"),
            "IS NOT EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"{property} != null"),
            // IN/BETWEEN parse a list rather than a single scalar; handle them out of the main switch.
            _ => ApplyInOrRange(query, property, op, value)
        };

    private static bool TryParse(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static IQueryable<T> ApplyInOrRange<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IN" => ApplyIn(query, property, value),
            "BETWEEN" => ApplyBetween(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseList(value, ParseInt);
        return values.Count == 0 ? query : query.Where(DynamicQueryConfig.Parameterized, $"@0.Contains({property})", values);
    }

    private static IQueryable<T> ApplyBetween<T>(IQueryable<T> query, string property, string value)
    {
        // BETWEEN takes exactly two comma-separated bounds ("min,max"), inclusive on both ends.
        var bounds = FilterValueParser.ParseList(value, ParseInt);
        return bounds.Count == 2
            ? query.Where(DynamicQueryConfig.Parameterized, $"{property} >= @0 && {property} <= @1", bounds[0], bounds[1])
            : query;
    }

    private static int? ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
