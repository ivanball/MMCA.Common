using System.Collections.Frozen;
using System.Globalization;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="decimal"/> and <see cref="Nullable{Decimal}"/> properties.
/// Supports equality and numeric comparison operators. Uses <see cref="CultureInfo.InvariantCulture"/>
/// for parsing to ensure consistent decimal separator handling across locales.
/// </summary>
internal sealed class DecimalFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "GREATER THAN", "LESS THAN",
        "GREATER THAN OR EQUAL", "LESS THAN OR EQUAL"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
    {
        if (!decimal.TryParse(value, CultureInfo.InvariantCulture, out var decimalValue))
            return query;

        return op switch
        {
            "EQUALS" => query.Where($"{property} == @0", decimalValue),
            "NOT EQUALS" => query.Where($"{property} != @0", decimalValue),
            "GREATER THAN" => query.Where($"{property} > @0", decimalValue),
            "LESS THAN" => query.Where($"{property} < @0", decimalValue),
            "GREATER THAN OR EQUAL" => query.Where($"{property} >= @0", decimalValue),
            "LESS THAN OR EQUAL" => query.Where($"{property} <= @0", decimalValue),
            _ => query
        };
    }
}
