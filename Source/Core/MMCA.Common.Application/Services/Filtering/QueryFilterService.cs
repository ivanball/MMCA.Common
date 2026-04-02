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

            // For nested paths like "Category.Name", resolve the root property to validate existence
            var rootPropertyName = entityProperty.Contains('.', StringComparison.Ordinal)
                ? entityProperty.Split('.')[0]
                : property;

            var propertyInfo = PropertyCache.GetOrAdd(
                (typeof(TEntity), property),
                static key => key.EntityType.GetProperty(key.PropertyName, BindingFlags.Public | BindingFlags.Instance))
                ?? PropertyCache.GetOrAdd(
                    (typeof(TEntity), rootPropertyName),
                    static key => key.EntityType.GetProperty(key.PropertyName, BindingFlags.Public | BindingFlags.Instance));

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

        foreach (var (property, (op, _)) in filters)
            ValidateSingleFilter<TEntity>(property, op, dtoToEntityPropertyMap, errors);

        return errors.Count == 0
            ? Result.Success()
            : Result.Failure(errors);
    }

    private static void ValidateSingleFilter<TEntity>(
        string property,
        string op,
        IReadOnlyDictionary<string, string> dtoToEntityPropertyMap,
        List<Error> errors)
    {
        var entityProperty = dtoToEntityPropertyMap.TryGetValue(property, out var mapped)
            ? mapped
            : property;

        var propertyName = entityProperty.Contains('.', StringComparison.Ordinal)
            ? entityProperty.Split('.')[0]
            : entityProperty;

        var propertyInfo = PropertyCache.GetOrAdd(
            (typeof(TEntity), property),
            static key => key.EntityType.GetProperty(key.PropertyName, BindingFlags.Public | BindingFlags.Instance))
            ?? PropertyCache.GetOrAdd(
                (typeof(TEntity), propertyName),
                static key => key.EntityType.GetProperty(key.PropertyName, BindingFlags.Public | BindingFlags.Instance));

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
            if (StringStrategy.SupportedOperators?.Contains(opUpper) == false)
            {
                errors.Add(Error.Validation(
                    "Filter.Operator.NotSupported",
                    $"Operator '{op}' is not supported for property '{property}' (type: string).",
                    source: nameof(ValidateFilters),
                    target: property));
            }

            return;
        }

        IFilterStrategy? strategy = propertyInfo.PropertyType == typeof(string)
            ? StringStrategy
            : Strategies.GetValueOrDefault(propertyInfo.PropertyType);

        if (strategy is null)
        {
            errors.Add(Error.Validation(
                "Filter.Type.NotSupported",
                $"No filter strategy registered for type '{propertyInfo.PropertyType.Name}' (property '{property}').",
                source: nameof(ValidateFilters),
                target: property));
            return;
        }

        if (strategy.SupportedOperators is not null && !strategy.SupportedOperators.Contains(opUpper))
        {
            errors.Add(Error.Validation(
                "Filter.Operator.NotSupported",
                $"Operator '{op}' is not supported for property '{property}' (type: {propertyInfo.PropertyType.Name}).",
                source: nameof(ValidateFilters),
                target: property));
        }
    }
}
