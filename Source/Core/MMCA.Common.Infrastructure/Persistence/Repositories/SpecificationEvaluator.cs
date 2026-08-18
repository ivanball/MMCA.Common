using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// Turns a specification into an <see cref="IQueryable{T}"/>: criteria always, plus the includes,
/// ordering and paging carried by a
/// <see cref="QuerySpecification{TEntity, TIdentifierType}"/>.
/// <para>
/// Tracking and soft-delete scope are deliberately NOT applied here: those choose the base queryable
/// (<c>Table</c> vs <c>TableNoTracking</c>, with or without the named soft-delete filter dropped),
/// which only the repository can do. The evaluator composes on top of whatever base it is handed.
/// </para>
/// </summary>
internal static class SpecificationEvaluator
{
    /// <summary>
    /// Applies a specification to a base queryable.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
    /// <param name="source">The base queryable (already scoped for tracking and query filters).</param>
    /// <param name="specification">The specification to apply.</param>
    /// <param name="applyShape">
    /// Whether to apply the includes, ordering, and paging of a
    /// <see cref="QuerySpecification{TEntity, TIdentifierType}"/>. Aggregate reads (count, exists)
    /// pass <see langword="false"/>: joining in includes to count rows costs a join per navigation,
    /// and counting "page 3 of the matches" is never what a caller means.
    /// </param>
    /// <returns>The composed queryable.</returns>
    internal static IQueryable<TEntity> Apply<TEntity, TIdentifierType>(
        IQueryable<TEntity> source,
        ISpecification<TEntity, TIdentifierType> specification,
        bool applyShape = true)
        where TEntity : class, IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specification);

        var query = source.Where(specification.Criteria);

        if (!applyShape || specification is not QuerySpecification<TEntity, TIdentifierType> querySpecification)
            return query;

        query = ApplyIncludes(query, querySpecification.IncludePaths);
        query = ApplyOrdering(query, querySpecification.OrderBy);

        if (querySpecification.Skip is int skip and > 0)
            query = query.Skip(skip);

        if (querySpecification.Take is int take)
            query = query.Take(take);

        return query;
    }

    /// <summary>
    /// Applies string-based eager loading includes to the query. Skips empty/whitespace entries.
    /// When any include targets a collection navigation the query opts into split-query mode so
    /// sibling collections do not multiply rows (cartesian explosion) under EF's default
    /// single-query JOIN strategy.
    /// </summary>
    /// <remarks>
    /// This is the single home of that heuristic: <c>EFReadRepository.ApplyIncludes</c> delegates
    /// here, so the string-include path and the specification path can never drift apart.
    /// </remarks>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The queryable to apply the includes to.</param>
    /// <param name="includes">The dot-separated navigation paths.</param>
    /// <returns>The queryable with the includes (and split-query mode when warranted) applied.</returns>
    internal static IQueryable<TEntity> ApplyIncludes<TEntity>(
        IQueryable<TEntity> query,
        IEnumerable<string> includes)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(includes);

        var hasCollectionInclude = false;

        foreach (string include in includes.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            query = query.Include(include);
            hasCollectionInclude = hasCollectionInclude || IsCollectionNavigationPath(typeof(TEntity), include);
        }

        return hasCollectionInclude ? query.AsSplitQuery() : query;
    }

    /// <summary>
    /// Applies an ordering chain: the first key becomes <c>OrderBy</c>/<c>OrderByDescending</c> and
    /// every later key a <c>ThenBy</c>/<c>ThenByDescending</c>. A specification with no ordering
    /// leaves the query untouched (the caller's own ordering, if any, survives).
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The queryable to order.</param>
    /// <param name="orderBy">The ordering keys, in application order.</param>
    /// <returns>The ordered queryable.</returns>
    internal static IQueryable<TEntity> ApplyOrdering<TEntity>(
        IQueryable<TEntity> query,
        IReadOnlyList<OrderExpression> orderBy)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(orderBy);

        for (var i = 0; i < orderBy.Count; i++)
        {
            query = ApplyOrderingStep(query, orderBy[i].KeySelector, orderBy[i].Descending, isFirst: i == 0);
        }

        return query;
    }

    /// <summary>
    /// Applies one ordering step, binding the untyped key selector back to its concrete key type.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="query">The queryable to order.</param>
    /// <param name="keySelector">The key selector lambda.</param>
    /// <param name="descending">Whether this key sorts descending.</param>
    /// <param name="isFirst">Whether this is the first key (<c>OrderBy</c>) or a later one (<c>ThenBy</c>).</param>
    /// <returns>The ordered queryable.</returns>
    internal static IQueryable<TEntity> ApplyOrderingStep<TEntity>(
        IQueryable<TEntity> query,
        LambdaExpression keySelector,
        bool descending,
        bool isFirst)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(keySelector);

        var name = (isFirst, descending) switch
        {
            (true, false) => nameof(Queryable.OrderBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (false, false) => nameof(Queryable.ThenBy),
            (false, true) => nameof(Queryable.ThenByDescending),
        };

        var method = OrderingMethods[name].MakeGenericMethod(typeof(TEntity), keySelector.ReturnType);
        return (IQueryable<TEntity>)method.Invoke(null, [query, keySelector])!;
    }

    /// <summary>
    /// The four two-argument <see cref="Queryable"/> ordering methods, resolved once. Each call site
    /// closes one over (entity type, key type); the reflection lookup itself never repeats.
    /// </summary>
    private static readonly Dictionary<string, MethodInfo> OrderingMethods =
        new[]
        {
            nameof(Queryable.OrderBy),
            nameof(Queryable.OrderByDescending),
            nameof(Queryable.ThenBy),
            nameof(Queryable.ThenByDescending),
        }.ToDictionary(
            name => name,
            name => typeof(Queryable)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => string.Equals(m.Name, name, StringComparison.Ordinal)
                    && m.GetGenericArguments().Length == 2
                    && m.GetParameters().Length == 2),
            StringComparer.Ordinal);

    /// <summary>
    /// Caches, per (entity type, include path), whether any segment of the path is a collection
    /// navigation. The reflection walk runs once per distinct pair; dispatch afterwards is a
    /// dictionary hit.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EntityType, string Path), bool> CollectionIncludeCache = new();

    private static bool IsCollectionNavigationPath(Type entityType, string includePath)
        => CollectionIncludeCache.GetOrAdd((entityType, includePath), static key =>
        {
            var type = key.EntityType;
            foreach (var segment in key.Path.Split('.'))
            {
                var property = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
                if (property is null)
                    return false; // unknown segment: leave the split decision to EF's own include validation

                var propertyType = property.PropertyType;
                if (propertyType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
                    return true;

                type = propertyType;
            }

            return false;
        });
}
