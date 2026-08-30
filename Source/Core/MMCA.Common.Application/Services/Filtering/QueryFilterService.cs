using System.Collections.Concurrent;
using System.Reflection;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Applies dynamic query filters using a strategy-per-type pattern.
/// Each .NET property type (string, int, DateTime, etc.) has a dedicated
/// <see cref="IFilterStrategy"/> that knows how to build LINQ Dynamic expressions
/// for its supported operators. Register additional strategies via
/// <see cref="RegisterStrategy"/> at startup for custom types.
/// <para>
/// Supports DTO-to-entity property name mapping and nested property filtering
/// (e.g. <c>"Category.Name"</c>). A nested path is walked segment by segment to its leaf, and the
/// leaf's type picks the strategy, so <c>"Category.Id"</c> filters as a Guid rather than as a
/// string. A path whose leaf cannot be reached is rejected by <see cref="ValidateFilters"/> as an
/// unknown property.
/// </para>
/// </summary>
public static class QueryFilterService
{
    /// <summary>
    /// Caches RESOLVED PropertyInfo lookups per (entity type, property name) to avoid per-request
    /// reflection overhead. Misses are deliberately not cached: the names probed come from the
    /// client's query string, so a negative cache is an unbounded, never-evicted static dictionary
    /// any caller can grow at will. See <see cref="LookupProperty{TEntity}"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EntityType, string PropertyName), PropertyInfo> PropertyCache = new();

    private static readonly ConcurrentDictionary<Type, IFilterStrategy> Strategies = new(
        new Dictionary<Type, IFilterStrategy>
        {
            [typeof(string)] = new StringFilterStrategy(),
            [typeof(bool)] = new BoolFilterStrategy(),
            [typeof(bool?)] = new BoolFilterStrategy(),
            [typeof(int)] = new IntFilterStrategy(),
            [typeof(int?)] = new IntFilterStrategy(),
            [typeof(long)] = new LongFilterStrategy(),
            [typeof(long?)] = new LongFilterStrategy(),
            [typeof(DateTime)] = new DateTimeFilterStrategy(),
            [typeof(DateTime?)] = new DateTimeFilterStrategy(),
            [typeof(decimal)] = new DecimalFilterStrategy(),
            [typeof(decimal?)] = new DecimalFilterStrategy(),
            [typeof(Guid)] = new GuidFilterStrategy(),
            [typeof(Guid?)] = new GuidFilterStrategy(),
        });

    /// <summary>
    /// Dedicated string strategy instance, used whenever the resolved value type is
    /// <see cref="string"/>: a flat string property, or a nested path whose leaf is one.
    /// </summary>
    private static readonly StringFilterStrategy StringStrategy = new();

    /// <summary>
    /// Registers a filter strategy for a property type, enabling extension without
    /// modifying existing code (e.g. Guid, long, custom value objects).
    /// </summary>
    /// <param name="propertyType">The CLR type this strategy handles.</param>
    /// <param name="strategy">The filter strategy implementation.</param>
    public static void RegisterStrategy(Type propertyType, IFilterStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(propertyType);
        ArgumentNullException.ThrowIfNull(strategy);
        Strategies[propertyType] = strategy;
    }

    /// <summary>
    /// Applies all filters to the query by resolving the appropriate <see cref="IFilterStrategy"/>
    /// for each filter property's type.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being queried.</typeparam>
    /// <param name="query">The base queryable to filter.</param>
    /// <param name="filters">Dictionary of property name to (operator, value) pairs.</param>
    /// <param name="dtoToEntityPropertyMap">Maps DTO property names to entity property paths.</param>
    /// <returns>The filtered queryable.</returns>
    public static IQueryable<TEntity> ApplyFilters<TEntity>(
        IQueryable<TEntity> query,
        Dictionary<string, (string Operator, string Value)> filters,
        IReadOnlyDictionary<string, string> dtoToEntityPropertyMap)
    {
        foreach (var (property, (op, value)) in filters)
        {
            // Resolve DTO property name to entity property path (e.g. "CategoryName" -> "Category.Name")
            var entityProperty = dtoToEntityPropertyMap.TryGetValue(property, out var mapped)
                ? mapped
                : property;

            var propertyInfo = ResolvePropertyInfo<TEntity>(property, entityProperty);

            if (propertyInfo is null)
                continue;

            // A dotted path whose leaf cannot be reached is skipped for the same reason an unknown
            // property is: ValidateFilters rejects both, so reaching here means the caller never
            // validated, and applying a filter the query cannot express is worse than dropping it.
            var valueType = ResolveFilterValueType<TEntity>(entityProperty, propertyInfo);
            if (valueType is null)
                continue;

            var opUpper = op.ToUpperInvariant();

            var strategy = ResolveStrategy(valueType);
            if (strategy is not null)
                query = strategy.Apply(query, entityProperty, opUpper, value);
        }

        return query;
    }

    /// <summary>
    /// Validates that all filter properties exist on the entity and that the requested
    /// operators are supported by the corresponding filter strategy.
    /// </summary>
    /// <typeparam name="TEntity">The entity type to validate against.</typeparam>
    /// <param name="filters">The filters to validate.</param>
    /// <param name="dtoToEntityPropertyMap">Maps DTO property names to entity property paths.</param>
    /// <returns>A success result, or a failure containing all validation errors.</returns>
    public static Result ValidateFilters<TEntity>(
        Dictionary<string, (string Operator, string Value)>? filters,
        IReadOnlyDictionary<string, string> dtoToEntityPropertyMap)
    {
        if (filters is null || filters.Count == 0)
            return Result.Success();

        List<Error> errors = [];

        foreach (var (property, (op, value)) in filters)
            ValidateSingleFilter<TEntity>(property, op, value, dtoToEntityPropertyMap, errors);

        return errors.Count == 0
            ? Result.Success()
            : Result.Failure(errors);
    }

    private static void ValidateSingleFilter<TEntity>(
        string property,
        string op,
        string value,
        IReadOnlyDictionary<string, string> dtoToEntityPropertyMap,
        List<Error> errors)
    {
        var entityProperty = dtoToEntityPropertyMap.TryGetValue(property, out var mapped)
            ? mapped
            : property;

        var propertyInfo = ResolvePropertyInfo<TEntity>(property, entityProperty);

        if (propertyInfo is null)
        {
            errors.Add(Error.Validation(
                "Filter.Property.NotFound",
                $"Filter property '{property}' does not exist on type '{typeof(TEntity).Name}'.",
                source: nameof(ValidateFilters),
                target: typeof(TEntity).Name));
            return;
        }

        var opUpper = op.ToUpperInvariant();

        // Resolve the type the filter VALUE is compared against, walking a nested path to its leaf.
        // Validation and application must agree on this: a nested non-string leaf routed to the
        // string strategy passes validation for a string-only operator (say IS EMPTY on
        // "Category.Id") and then fails inside Dynamic LINQ at query-build time, which is a 500 for
        // what is really a bad request.
        var valueType = ResolveFilterValueType<TEntity>(entityProperty, propertyInfo);

        // A path whose leaf cannot be reached names a property the entity does not have, so it is
        // the same failure an unknown flat property gets: a 400 naming the path, never a silent
        // fall back to the string strategy on a filter the query cannot express.
        if (valueType is null)
        {
            errors.Add(Error.Validation(
                "Filter.Property.NotFound",
                $"Filter property path '{entityProperty}' does not resolve on type '{typeof(TEntity).Name}' (property '{property}').",
                source: nameof(ValidateFilters),
                target: typeof(TEntity).Name));
            return;
        }

        var strategy = ResolveStrategy(valueType);

        if (strategy is null)
        {
            errors.Add(Error.Validation(
                "Filter.Type.NotSupported",
                $"No filter strategy registered for type '{valueType.Name}' (property '{property}').",
                source: nameof(ValidateFilters),
                target: property));
            return;
        }

        var typeName = valueType.Name;
        ValidateOperatorSupported(strategy, opUpper, op, property, typeName, errors);
        ValidateValueParseable(strategy, opUpper, value, property, typeName, errors);
    }

    /// <summary>
    /// Rejects a filter whose value the strategy cannot apply. Without this the strategy returns the
    /// query unfiltered, so a malformed value widened the response to the full result set instead of
    /// narrowing it to no matches.
    /// </summary>
    private static void ValidateValueParseable(
        IFilterStrategy strategy,
        string opUpper,
        string value,
        string property,
        string typeName,
        List<Error> errors)
    {
        // Only complain about the value once the operator itself is valid, so one bad filter does
        // not produce two errors describing the same mistake.
        if (strategy.SupportedOperators is not null && !strategy.SupportedOperators.Contains(opUpper))
            return;

        if (!strategy.CanParseValue(opUpper, value))
        {
            errors.Add(Error.Validation(
                "Filter.Value.Invalid",
                $"Filter value '{value}' is not valid for property '{property}' (type: {typeName}) with operator '{opUpper}'.",
                source: nameof(ValidateFilters),
                target: property));
        }
    }

    /// <summary>
    /// Resolves the <see cref="PropertyInfo"/> backing a filter: the DTO-facing name first, then the
    /// mapped entity name (its root segment for a nested path like <c>"Category.Name"</c>).
    /// <para>
    /// Shared by <see cref="ApplyFilters"/> and <see cref="ValidateFilters"/> so both agree on what
    /// resolves. They used to disagree on the fallback: validation tried the mapped entity name
    /// while application retried the DTO name, so a plain rename entry (for example
    /// <c>["Name"] = "Title"</c>) passed validation and was then silently dropped, returning an
    /// unfiltered result set with a 200.
    /// </para>
    /// <para>
    /// Only SUCCESSFUL lookups are cached. Both names probed here are client-influenced (the filter
    /// key arrives in the query string), so caching misses too let any caller grow a process-lifetime
    /// static dictionary without bound simply by filtering on names that do not exist: the request
    /// gets a clean 400 back, which makes the growth invisible in error metrics. A miss now costs a
    /// reflection lookup instead, which the per-request filter cap in <c>QueryFilterModelBinder</c>
    /// bounds.
    /// </para>
    /// </summary>
    private static PropertyInfo? ResolvePropertyInfo<TEntity>(string property, string entityProperty)
    {
        var propertyName = entityProperty.Contains('.', StringComparison.Ordinal)
            ? entityProperty.Split('.')[0]
            : entityProperty;

        return LookupProperty<TEntity>(property) ?? LookupProperty<TEntity>(propertyName);
    }

    /// <summary>
    /// Resolves one property name against <typeparamref name="TEntity"/>, memoizing hits only.
    /// </summary>
    private static PropertyInfo? LookupProperty<TEntity>(string propertyName)
    {
        var key = (typeof(TEntity), propertyName);
        if (PropertyCache.TryGetValue(key, out var cached))
            return cached;

        var resolved = typeof(TEntity).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (resolved is not null)
            PropertyCache[key] = resolved;

        return resolved;
    }

    /// <summary>
    /// Resolves the type a filter value is compared against: for a flat property that is the
    /// property's own type, and for a dotted path (<c>"Category.Name"</c>) it is the LEAF's type,
    /// reached by walking each segment.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the path cannot be walked (a segment is not a public
    /// instance property of the preceding segment's type). That is a filter the query can never
    /// answer, so the caller fails it closed: validation rejects it as an unknown property and
    /// application skips it, rather than guessing at the string strategy and letting Dynamic LINQ
    /// turn a bad request into a 500 at query-build time.
    /// </remarks>
    private static Type? ResolveFilterValueType<TEntity>(string entityProperty, PropertyInfo resolvedRoot)
    {
        if (!entityProperty.Contains('.', StringComparison.Ordinal))
            return resolvedRoot.PropertyType;

        var segments = entityProperty.Split('.');

        // Walk from the path's own root segment rather than from resolvedRoot: the latter may have
        // matched the DTO-facing name instead, and for a nested path the two need not agree.
        var current = LookupProperty<TEntity>(segments[0])?.PropertyType;

        for (var i = 1; i < segments.Length && current is not null; i++)
        {
            current = current
                .GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance)
                ?.PropertyType;
        }

        return current;
    }

    private static IFilterStrategy? ResolveStrategy(Type propertyType) =>
        propertyType == typeof(string)
            ? StringStrategy
            : Strategies.GetValueOrDefault(propertyType);

    private static void ValidateOperatorSupported(
        IFilterStrategy strategy,
        string opUpper,
        string originalOp,
        string property,
        string typeName,
        List<Error> errors)
    {
        if (strategy.SupportedOperators is not null && !strategy.SupportedOperators.Contains(opUpper))
        {
            errors.Add(Error.Validation(
                "Filter.Operator.NotSupported",
                $"Operator '{originalOp}' is not supported for property '{property}' (type: {typeName}).",
                source: nameof(ValidateFilters),
                target: property));
        }
    }
}
