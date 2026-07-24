using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="decimal"/> and <see cref="Nullable{Decimal}"/> properties.
/// Supports equality and numeric comparison operators, the comma-separated IN set, an inclusive
/// BETWEEN range, and the IS EMPTY / IS NOT EMPTY null checks. Uses
/// <see cref="CultureInfo.InvariantCulture"/> for parsing to ensure consistent decimal separator
/// handling across locales.
/// </summary>
internal sealed class DecimalFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "GREATER THAN", "LESS THAN",
        "GREATER THAN OR EQUAL", "LESS THAN OR EQUAL", "IN", "BETWEEN",
        "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool CanParseValue(string op, string value) =>
        FilterValueParser.CanParse(op, value, ParseDecimal);

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

    private static bool TryParse(string value, out decimal result) =>
        decimal.TryParse(value, CultureInfo.InvariantCulture, out result);

    private static IQueryable<T> ApplyInOrRange<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IN" => ApplyIn(query, property, value),
            "BETWEEN" => ApplyBetween(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseList(value, ParseDecimal);
        return values.Count == 0 ? query : query.Where($"@0.Contains({property})", values);
    }

    private static IQueryable<T> ApplyBetween<T>(IQueryable<T> query, string property, string value)
    {
        // BETWEEN takes exactly two comma-separated bounds ("min,max"), inclusive on both ends.
        var bounds = FilterValueParser.ParseList(value, ParseDecimal);
        return bounds.Count == 2
            ? query.Where($"{property} >= @0 && {property} <= @1", bounds[0], bounds[1])
            : query;
    }

    private static decimal? ParseDecimal(string s) =>
        decimal.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
}
