using System.Collections.Frozen;
using System.Linq.Dynamic.Core;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for <see cref="Guid"/> and <see cref="Nullable{Guid}"/> properties.
/// Supports equality operators, the comma-separated IN set, and the IS EMPTY / IS NOT EMPTY null
/// checks (meaningful for <see cref="Nullable{Guid}"/> columns). GUIDs have no meaningful ordering,
/// so no comparison or range operators are provided. Silently returns the unfiltered query if the
/// value cannot be parsed as a GUID.
/// </summary>
internal sealed class GuidFilterStrategy : IFilterStrategy
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "IN", "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "EQUALS" when Guid.TryParse(value, out var g) => query.Where($"{property} == @0", g),
            "NOT EQUALS" when Guid.TryParse(value, out var g) => query.Where($"{property} != @0", g),
            "IS EMPTY" => query.Where($"{property} == null"),
            "IS NOT EMPTY" => query.Where($"{property} != null"),
            // IN parses a comma-separated set rather than a single scalar.
            "IN" => ApplyIn(query, property, value),
            _ => query
        };

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseList(value, static s => Guid.TryParse(s, out var g) ? g : (Guid?)null);
        return values.Count == 0 ? query : query.Where($"@0.Contains({property})", values);
    }
}
