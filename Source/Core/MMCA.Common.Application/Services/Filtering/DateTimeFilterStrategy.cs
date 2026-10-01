using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="DateTime"/> and <see cref="Nullable{DateTime}"/> properties.
/// Supports temporal comparison operators (IS, IS AFTER, IS BEFORE, etc.), null checks, the
/// comma-separated IN set, and an inclusive BETWEEN range. All date parsing uses
/// <see cref="CultureInfo.InvariantCulture"/> to ensure consistent behavior across server locales.
/// </summary>
internal sealed class DateTimeFilterStrategy : IFilterStrategy
{
    private static readonly IFormatProvider FormatProvider = CultureInfo.InvariantCulture;

    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "IS", "IS NOT", "IS AFTER", "IS ON OR AFTER",
        "IS BEFORE", "IS ON OR BEFORE", "IS EMPTY", "IS NOT EMPTY",
        "IN", "BETWEEN"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool CanParseValue(string op, string value) =>
        FilterValueParser.CanParse(op, value, ParseDateTime);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IS" when ParseDateTime(value) is { } dt
                => query.Where(DynamicQueryConfig.Parameterized, $"{property} == @0", dt),
            "IS NOT" when ParseDateTime(value) is { } dt
                => query.Where(DynamicQueryConfig.Parameterized, $"{property} != @0", dt),
            "IS AFTER" when ParseDateTime(value) is { } dt
                => query.Where(DynamicQueryConfig.Parameterized, $"{property} > @0", dt),
            "IS ON OR AFTER" when ParseDateTime(value) is { } dt
                => query.Where(DynamicQueryConfig.Parameterized, $"{property} >= @0", dt),
            "IS BEFORE" when ParseDateTime(value) is { } dt
                => query.Where(DynamicQueryConfig.Parameterized, $"{property} < @0", dt),
            "IS ON OR BEFORE" when ParseDateTime(value) is { } dt
                => query.Where(DynamicQueryConfig.Parameterized, $"{property} <= @0", dt),
            "IS EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"{property} == null"),
            "IS NOT EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"{property} != null"),
            // IN/BETWEEN parse a list rather than a single scalar; handle them out of the main switch.
            _ => ApplyInOrRange(query, property, op, value)
        };

    private static IQueryable<T> ApplyInOrRange<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IN" => ApplyIn(query, property, value),
            "BETWEEN" => ApplyBetween(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseList(value, ParseDateTime);
        return values.Count == 0 ? query : query.Where(DynamicQueryConfig.Parameterized, $"@0.Contains({property})", values);
    }

    private static IQueryable<T> ApplyBetween<T>(IQueryable<T> query, string property, string value)
    {
        // BETWEEN takes exactly two comma-separated bounds ("min,max"), inclusive on both ends.
        var bounds = FilterValueParser.ParseList(value, ParseDateTime);
        return bounds.Count == 2
            ? query.Where(DynamicQueryConfig.Parameterized, $"{property} >= @0 && {property} <= @1", bounds[0], bounds[1])
            : query;
    }

    /// <summary>
    /// Parses one filter bound as a UTC instant, the way the framework stores every
    /// <see cref="DateTime"/>: an offset or <c>Z</c> is honoured and normalized to UTC, and a bare
    /// value is taken as UTC. The host's local time zone never enters the result, so the same filter
    /// selects the same rows on every server.
    /// </summary>
    /// <param name="s">The bound as sent by the client.</param>
    /// <returns>The UTC instant (<see cref="DateTimeKind.Utc"/>), or <see langword="null"/> when it does not parse.</returns>
    internal static DateTime? ParseDateTime(string s) =>
        DateTime.TryParse(s, FormatProvider, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
}
