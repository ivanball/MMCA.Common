using System.Collections.Concurrent;
using System.Dynamic;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Provides field validation, data shaping (field projection on DTOs), dynamic sorting,
/// and server-side field selection (builds <c>Select</c> expressions for EF Core).
/// Caches reflected property metadata per type to avoid repeated reflection.
/// </summary>
public sealed class QueryFieldService
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertiesCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyAccessor[]> AccessorCache = new();
    private static readonly JsonNamingPolicy CamelCase = JsonNamingPolicy.CamelCase;

    /// <summary>Pre-compiled property getter with camelCase name, replacing <c>PropertyInfo.GetValue()</c> in hot paths.</summary>
    private readonly record struct PropertyAccessor(string PropertyName, string CamelCaseName, Func<object, object?> GetValue);

    private static PropertyAccessor[] GetAccessors<TEntity>()
        => AccessorCache.GetOrAdd(typeof(TEntity), static t =>
        {
            var properties = PropertiesCache.GetOrAdd(t, static tt => tt.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            var accessors = new PropertyAccessor[properties.Length];
            for (var i = 0; i < properties.Length; i++)
            {
                var prop = properties[i];
                var param = Expression.Parameter(typeof(object), "obj");
                var castObj = Expression.Convert(param, t);
                var access = Expression.Property(castObj, prop);
                var boxed = Expression.Convert(access, typeof(object));
                var getter = Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
                accessors[i] = new PropertyAccessor(prop.Name, CamelCase.ConvertName(prop.Name), getter);
            }

            return accessors;
        });

    /// <summary>
    /// Shapes a single entity into an <see cref="ExpandoObject"/> containing only the requested fields.
    /// If <paramref name="fields"/> is null or empty, all properties are included.
    /// </summary>
    /// <typeparam name="TEntity">The entity/DTO type.</typeparam>
    /// <param name="entity">The entity to shape.</param>
    /// <param name="fields">Comma-separated field names, or null for all fields.</param>
    /// <returns>A dynamic object containing the selected properties.</returns>
    public static ExpandoObject ShapeData<TEntity>(TEntity entity, string? fields)
    {
        var accessors = GetShapedAccessors<TEntity>(fields);

        IDictionary<string, object?> shapedObject = new ExpandoObject();

        foreach (var accessor in accessors)
        {
            shapedObject[accessor.CamelCaseName] = accessor.GetValue(entity!);
        }

        return (ExpandoObject)shapedObject;
    }

    /// <summary>
    /// Shapes a collection of entities into <see cref="ExpandoObject"/> instances containing only the requested fields.
    /// </summary>
    /// <typeparam name="TEntity">The entity/DTO type.</typeparam>
    /// <param name="entities">The entities to shape.</param>
    /// <param name="fields">Comma-separated field names, or null for all fields.</param>
    /// <returns>A list of dynamic objects containing the selected properties.</returns>
    public static List<ExpandoObject> ShapeCollectionData<TEntity>(
        IEnumerable<TEntity> entities,
        string? fields)
    {
        var accessors = GetShapedAccessors<TEntity>(fields);

        List<ExpandoObject> shapedObjects = [];

        foreach (TEntity entity in entities)
        {
            IDictionary<string, object?> shapedObject = new ExpandoObject();

            foreach (var accessor in accessors)
            {
                shapedObject[accessor.CamelCaseName] = accessor.GetValue(entity!);
            }

            shapedObjects.Add((ExpandoObject)shapedObject);
        }

        return shapedObjects;
    }

    /// <summary>
    /// Applies dynamic sorting via LINQ Dynamic. Resolves DTO property names through
    /// <paramref name="dtoToEntityPropertyMap"/> before building the sort expression.
    /// A sort column with no map entry is accepted only when it names a real public property
    /// of <typeparamref name="TEntity"/> (the same contract filter validation applies);
    /// anything else falls back to <paramref name="defaultSort"/> instead of reaching Dynamic
    /// LINQ, so a client-supplied string can never order by nested paths or expressions the
    /// DTO does not expose (hidden-column data inference, unindexed-sort load, parse-error 500s).
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The queryable to sort.</param>
    /// <param name="sortColumn">The property name to sort by.</param>
    /// <param name="sortDirection">"asc" or "desc".</param>
    /// <param name="dtoToEntityPropertyMap">DTO-to-entity property name mapping (server-authored; entries may be navigation paths or expressions).</param>
    /// <param name="defaultSort">Fallback sort expression when no valid sort column is specified.</param>
    /// <returns>The sorted queryable.</returns>
    public static IQueryable<TEntity> ApplySorting<TEntity>(
        IQueryable<TEntity> query,
        string? sortColumn,
        string? sortDirection,
        IReadOnlyDictionary<string, string> dtoToEntityPropertyMap,
        Expression<Func<TEntity, object>>? defaultSort = null)
    {
        if (!string.IsNullOrWhiteSpace(sortColumn))
        {
            var sortExpr = dtoToEntityPropertyMap.TryGetValue(sortColumn, out var mapped)
                ? mapped
                : typeof(TEntity).GetProperty(
                    sortColumn,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.Name;

            if (sortExpr is not null)
            {
                var descending = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
                return query.OrderBy(Filtering.DynamicQueryConfig.Parameterized, $"{sortExpr} {(descending ? "descending" : "ascending")}");
            }
        }

        return defaultSort is not null ? query.OrderBy(defaultSort) : query;
    }

    /// <summary>
    /// Builds a <c>MemberInit</c> expression to project only the requested fields server-side,
    /// reducing data transferred from the database. Only writable properties can be projected
    /// (read-only/computed properties would fail EF Core translation).
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The queryable to apply field selection to.</param>
    /// <param name="fields">Comma-separated field names, or null to skip projection.</param>
    /// <returns>The queryable with field selection applied.</returns>
    public static IQueryable<TEntity> ApplyFieldSelection<TEntity>(
        IQueryable<TEntity> query,
        string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return query;

        var fieldsSet = ParseFields(fields);
        var selectExpression = ProjectionCache.GetOrAdd(
            (typeof(TEntity), NormalizeFieldsKey(fieldsSet)),
            static (_, set) => BuildProjection<TEntity>(set),
            fieldsSet);

        return selectExpression is null
            ? query
            : query.Select((Expression<Func<TEntity, TEntity>>)selectExpression);
    }

    /// <summary>
    /// Compiled projection lambdas, keyed by entity type and normalized field set.
    /// </summary>
    /// <remarks>
    /// Rebuilding the <c>MemberInit</c> tree per request meant reflecting over the entity's
    /// properties and allocating a binding array on every field-projected read, unlike the sibling
    /// lookup-selector cache in <c>EFReadRepository</c>. A <see langword="null"/> value is cached
    /// deliberately: it records "this field set projects nothing writable", so the miss is not
    /// recomputed on every subsequent request.
    /// </remarks>
    private static readonly ConcurrentDictionary<(Type EntityType, string Fields), LambdaExpression?> ProjectionCache = new();

    private static Expression<Func<TEntity, TEntity>>? BuildProjection<TEntity>(HashSet<string> fieldsSet)
    {
        PropertyInfo[] selectedProperties =
        [
            .. GetProperties<TEntity>()
                .Where(p => p.CanWrite && fieldsSet.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
        ];

        if (selectedProperties.Length == 0)
            return null;

        ParameterExpression parameter = Expression.Parameter(typeof(TEntity), "e");

        MemberBinding[] bindings =
        [
            .. selectedProperties
                .Select(p => Expression.Bind(p, Expression.Property(parameter, p)))
        ];

        NewExpression newExpression = Expression.New(typeof(TEntity));
        MemberInitExpression body = Expression.MemberInit(newExpression, bindings);
        return Expression.Lambda<Func<TEntity, TEntity>>(body, parameter);
    }

    /// <summary>
    /// Validates that all requested field names exist on the entity type.
    /// When <paramref name="allowWriteableFields"/> is false, read-only properties are rejected
    /// (they cannot be used for data shaping since the projection MemberInit requires setters).
    /// </summary>
    /// <typeparam name="TEntity">The entity type to validate fields against.</typeparam>
    /// <param name="fields">Comma-separated field names to validate.</param>
    /// <param name="allowWriteableFields">If false, rejects read-only properties.</param>
    /// <returns>A success result, or a failure with validation errors.</returns>
    public static Result Validate<TEntity>(string? fields, bool allowWriteableFields = false)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return Result.Success();

        var fieldsSet = ParseFields(fields);
        var propertyInfos = GetProperties<TEntity>();

        List<Error> errors = [];

        foreach (string field in fieldsSet)
        {
            PropertyInfo? property = propertyInfos
                .FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase));

            if (property is null)
            {
                errors.Add(Error.InvalidEntityField with
                {
                    Message = $"Field '{field}' does not exist on type '{typeof(TEntity).Name}'.",
                    Target = typeof(TEntity).Name
                });

                continue;
            }

            if (!allowWriteableFields && !property.CanWrite)
            {
                errors.Add(Error.InvalidEntityField with
                {
                    Message = $"Field '{field}' on type '{typeof(TEntity).Name}' is read-only and cannot be used for data shaping.",
                    Target = typeof(TEntity).Name
                });
            }
        }

        return errors.Count == 0
            ? Result.Success()
            : Result.Failure(errors);
    }

    /// <summary>
    /// Validates that the sort direction is either "asc", "desc", or null/empty.
    /// </summary>
    /// <param name="sortDirection">The sort direction to validate.</param>
    /// <returns>A success result, or a validation error.</returns>
    public static Result ValidateSortDirection(string? sortDirection)
    {
        if (string.IsNullOrWhiteSpace(sortDirection))
            return Result.Success();

        if (string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        List<Error> errors =
        [
            Error.Validation(
                "Error.InvalidSortDirection",
                $"The provided sort direction '{sortDirection}' isn't valid. Allowed values are 'asc' or 'desc'.")
        ];

        return Result.Failure(errors);
    }

    private static HashSet<string> ParseFields(string? fields)
        => fields?
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

    private static PropertyInfo[] GetProperties<TEntity>()
        => PropertiesCache.GetOrAdd(
            typeof(TEntity),
            t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));

    private static PropertyAccessor[] FilterAccessorsByFields(
        PropertyAccessor[] accessors,
        HashSet<string> fieldsSet)
        => [
            .. accessors
                .Where(a => fieldsSet.Contains(a.PropertyName, StringComparer.OrdinalIgnoreCase))
        ];

    /// <summary>
    /// Shaping accessors filtered to a field set, keyed by entity type and normalized field set,
    /// so a repeated <c>fields=</c> request reuses the array instead of re-filtering per call.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EntityType, string Fields), PropertyAccessor[]> ShapedAccessorCache = new();

    /// <summary>
    /// Resolves the accessors to shape with: all of them when no field list was given, otherwise
    /// the cached subset for that field set.
    /// </summary>
    private static PropertyAccessor[] GetShapedAccessors<TEntity>(string? fields)
    {
        var accessors = GetAccessors<TEntity>();
        var fieldsSet = ParseFields(fields);

        return fieldsSet.Count == 0
            ? accessors
            : ShapedAccessorCache.GetOrAdd(
                (typeof(TEntity), NormalizeFieldsKey(fieldsSet)),
                static (_, arg) => FilterAccessorsByFields(arg.Accessors, arg.FieldsSet),
                (Accessors: accessors, FieldsSet: fieldsSet));
    }

    /// <summary>
    /// Order- and case-insensitive cache key, so "name,id" and "Id, Name" share one entry rather
    /// than multiplying the cache by however many spellings callers happen to send.
    /// </summary>
    private static string NormalizeFieldsKey(HashSet<string> fieldsSet) =>
        string.Join(',', fieldsSet.Select(f => f.ToUpperInvariant()).Order(StringComparer.Ordinal));
}
