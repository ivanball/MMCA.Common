using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="DateTime"/> and <see cref="Nullable{DateTime}"/> properties.
/// Supports temporal comparison operators (IS, IS AFTER, IS BEFORE, etc.) and null checks.
/// All date parsing uses <see cref="CultureInfo.InvariantCulture"/> to ensure consistent
/// behavior across server locales.
/// </summary>
internal sealed class DateTimeFilterStrategy : IFilterStrategy
{
    private static readonly IFormatProvider FormatProvider = CultureInfo.InvariantCulture;

    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "IS", "IS NOT", "IS AFTER", "IS ON OR AFTER",
        "IS BEFORE", "IS ON OR BEFORE", "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "IS" when DateTime.TryParse(value, FormatProvider, DateTimeStyles.None, out var dt)
                => query.Where($"{property} == @0", dt),
            "IS NOT" when DateTime.TryParse(value, FormatProvider, DateTimeStyles.None, out var dt)
                => query.Where($"{property} != @0", dt),
            "IS AFTER" when DateTime.TryParse(value, FormatProvider, DateTimeStyles.None, out var dt)
                => query.Where($"{property} > @0", dt),
            "IS ON OR AFTER" when DateTime.TryParse(value, FormatProvider, DateTimeStyles.None, out var dt)
                => query.Where($"{property} >= @0", dt),
            "IS BEFORE" when DateTime.TryParse(value, FormatProvider, DateTimeStyles.None, out var dt)
                => query.Where($"{property} < @0", dt),
            "IS ON OR BEFORE" when DateTime.TryParse(value, FormatProvider, DateTimeStyles.None, out var dt)
                => query.Where($"{property} <= @0", dt),
            "IS EMPTY" => query.Where($"{property} == null"),
            "IS NOT EMPTY" => query.Where($"{property} != null"),
            _ => query
        };
}
