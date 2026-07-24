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
/// (e.g. <c>"Category.Name"</c>), which always routes through the string strategy
/// since the nested value is accessed via the string expression path.
/// </para>
/// </summary>
public static class QueryFilterService
{
    /// <summary>
    /// Caches PropertyInfo lookups per (entity type, property name) to avoid per-request reflection overhead.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EntityType, string PropertyName), PropertyInfo?> PropertyCache = new();

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
    /// Dedicated string strategy instance used for both string properties and nested
    /// property paths (dot-separated), since nested access is always string-based in
    /// LINQ Dynamic expressions.
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

            var opUpper = op.ToUpperInvariant();

            // Nested properties (e.g. "Category.Name") always use string filtering because
            // the LINQ Dynamic expression path traverses the property chain as a string
            if (propertyInfo.PropertyType == typeof(string) ||
                entityProperty.Contains('.', StringComparison.OrdinalIgnoreCase))
            {
                query = StringStrategy.Apply(query, entityProperty, opUpper, value);
                continue;
            }

            if (Strategies.TryGetValue(propertyInfo.PropertyType, out var strategy))
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

        // Nested properties use string filtering
        if (entityProperty.Contains('.', StringComparison.Ordinal))
        {
            ValidateOperatorSupported(StringStrategy, opUpper, op, property, "string", errors);
            ValidateValueParseable(StringStrategy, opUpper, value, property, "string", errors);
            return;
        }

        var strategy = ResolveStrategy(propertyInfo.PropertyType);

        if (strategy is null)
        {
            errors.Add(Error.Validation(
                "Filter.Type.NotSupported",
                $"No filter strategy registered for type '{propertyInfo.PropertyType.Name}' (property '{property}').",
                source: nameof(ValidateFilters),
                target: property));
            return;
        }

        ValidateOperatorSupported(strategy, opUpper, op, property, propertyInfo.PropertyType.Name, errors);
        ValidateValueParseable(strategy, opUpper, value, property, propertyInfo.PropertyType.Name, errors);
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
    /// </summary>
    private static PropertyInfo? ResolvePropertyInfo<TEntity>(string property, string entityProperty)
    {
        var propertyName = entityProperty.Contains('.', StringComparison.Ordinal)
            ? entityProperty.Split('.')[0]
            : entityProperty;

        return PropertyCache.GetOrAdd(
            (typeof(TEntity), property),
            static key => key.EntityType.GetProperty(key.PropertyName, BindingFlags.Public | BindingFlags.Instance))
            ?? PropertyCache.GetOrAdd(
                (typeof(TEntity), propertyName),
                static key => key.EntityType.GetProperty(key.PropertyName, BindingFlags.Public | BindingFlags.Instance));
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
