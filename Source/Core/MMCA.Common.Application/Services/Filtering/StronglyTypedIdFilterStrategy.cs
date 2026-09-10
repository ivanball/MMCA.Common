using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Dynamic.Core;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Filter strategy for a column typed as a strongly typed identifier (ADR-115). The client still
/// sends the primitive (<c>filters[Id].value=42</c>), the strategy parses it into the wrapper, and
/// the query compares wrapper to wrapper so EF Core's value converter turns both sides into the same
/// primitive column. Adopting a wrapper therefore does not change the filter DSL a client speaks.
/// <para>
/// Only the equality family is supported: EQUALS, NOT EQUALS, IN, and the IS EMPTY / IS NOT EMPTY
/// null checks. A record struct declares <c>==</c> and <c>!=</c> and nothing else, so there is no
/// <c>&gt;</c> to build a range predicate from, and ordering comparisons on an opaque identifier are
/// not a thing a caller should be asking for. An unsupported operator is refused by
/// <c>QueryFilterService.ValidateFilters</c> as a 400 rather than silently widening the result set.
/// </para>
/// </summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
/// <typeparam name="TValue">The wrapped primitive.</typeparam>
internal sealed class StronglyTypedIdFilterStrategy<TSelf, TValue> : IFilterStrategy
    where TSelf : struct, IStronglyTypedId<TSelf, TValue>
    where TValue : notnull, IEquatable<TValue>
{
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EQUALS", "NOT EQUALS", "IN", "IS EMPTY", "IS NOT EMPTY"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool CanParseValue(string op, string value)
        => FilterValueParser.CanParse(op, value, ParseIdentifier);

    /// <inheritdoc />
    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
        => op switch
        {
            "EQUALS" when TryParse(value, out var id) => query.Where(DynamicQueryConfig.Parameterized, $"{property} == @0", id),
            "NOT EQUALS" when TryParse(value, out var id) => query.Where(DynamicQueryConfig.Parameterized, $"{property} != @0", id),
            "IS EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"{property} == null"),
            "IS NOT EMPTY" => query.Where(DynamicQueryConfig.Parameterized, $"{property} != null"),
            "IN" => ApplyIn(query, property, value),
            _ => query
        };

    private static bool TryParse(string value, out TSelf result)
        => StronglyTypedId.TryParse<TSelf, TValue>(value, CultureInfo.InvariantCulture, out result);

    private static IQueryable<T> ApplyIn<T>(IQueryable<T> query, string property, string value)
    {
        var values = FilterValueParser.ParseList(value, ParseIdentifier);
        return values.Count == 0
            ? query
            : query.Where(DynamicQueryConfig.Parameterized, $"@0.Contains({property})", values);
    }

    private static TSelf? ParseIdentifier(string candidate)
        => TryParse(candidate, out var id) ? id : null;
}

/// <summary>
/// Builds the closed <see cref="StronglyTypedIdFilterStrategy{TSelf, TValue}"/> for a property type
/// at runtime. <c>QueryFilterService</c> calls this on a filter key whose resolved type has no
/// registered strategy, so a consumer that adopts wrappers never has to call
/// <c>RegisterStrategy</c> once per identifier.
/// </summary>
internal static class StronglyTypedIdFilterStrategy
{
    /// <summary>
    /// Creates the strategy for <paramref name="propertyType"/> when it is (or wraps in
    /// <see cref="Nullable{T}"/>) a strongly typed identifier.
    /// </summary>
    /// <param name="propertyType">The resolved filter value type.</param>
    /// <param name="strategy">The strategy when the type is an identifier.</param>
    /// <returns><see langword="true"/> when a strategy was created.</returns>
    internal static bool TryCreate(Type propertyType, [NotNullWhen(true)] out IFilterStrategy? strategy)
    {
        if (StronglyTypedId.TryDescribe(propertyType, out var identifierType, out var valueType))
        {
            strategy = (IFilterStrategy)Activator.CreateInstance(
                typeof(StronglyTypedIdFilterStrategy<,>).MakeGenericType(identifierType, valueType))!;
            return true;
        }

        strategy = null;
        return false;
    }
}
