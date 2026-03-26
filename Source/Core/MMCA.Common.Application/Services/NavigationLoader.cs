using System.Collections.Concurrent;
using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Batch-loads related entities for a collection of parents, avoiding N+1 query problems.
/// Supports two relationship types:
/// <list type="bullet">
///   <item><see cref="LoadFKPropertyAsync{TEntity,TChildEntity,TChildIdentifierType}"/> — loads FK navigations
///     (parent references a child via a nullable FK, e.g. Product.CategoryId -> Category).</item>
///   <item><see cref="LoadChildrenPropertyAsync{TEntity,TParentIdentifierType,TChildEntity,TChildIdentifierType}"/> — loads child collections
///     (children reference the parent via FK, e.g. Order -> OrderLines).</item>
/// </list>
/// Both methods build a <c>WHERE childFK IN (...parentIds)</c> expression tree at runtime
/// to fetch all related entities in a single query, then group results into a lookup dictionary.
/// Compiled expression delegates are cached to avoid repeated compilation overhead.
/// </summary>
public static class NavigationLoader
{
    /// <summary>
    /// Caches compiled expression delegates keyed by source type + expression body string,
    /// so that repeated calls for the same FK selector expression avoid recompilation.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Delegate> CompiledExpressionCache = new();

    /// <summary>
    /// Batch-loads FK navigation properties for a collection of parent entities.
    /// Collects distinct FK values from parents, executes a single <c>WHERE FK IN (...)</c>
    /// query against the child repository, then assigns results back to each parent.
    /// </summary>
    /// <typeparam name="TEntity">The parent entity type.</typeparam>
    /// <typeparam name="TChildEntity">The related (FK target) entity type.</typeparam>
    /// <typeparam name="TChildIdentifierType">The FK/identifier type (must be a value type for nullable support).</typeparam>
    /// <param name="parents">The parent entities to populate.</param>
    /// <param name="parentKeySelector">Extracts the nullable FK value from each parent.</param>
    /// <param name="childForeignKeySelector">Expression selecting the FK property on the child entity (used to build the WHERE clause).</param>
    /// <param name="childRepository">Repository to query the child entities from.</param>
    /// <param name="assignAction">Callback to assign the loaded children to each parent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all parents have been populated.</returns>
    public static async Task LoadFKPropertyAsync<TEntity, TChildEntity, TChildIdentifierType>(
        IReadOnlyCollection<TEntity> parents,
        Func<TEntity, TChildIdentifierType?> parentKeySelector,
        Expression<Func<TChildEntity, TChildIdentifierType>> childForeignKeySelector,
        IReadRepository<TChildEntity, TChildIdentifierType> childRepository,
        Action<TEntity, List<TChildEntity>> assignAction,
        CancellationToken cancellationToken)
        where TChildEntity : AuditableBaseEntity<TChildIdentifierType>
        where TChildIdentifierType : struct
    {
        var parentIds = parents
            .Select(parentKeySelector)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (parentIds.Count == 0)
        {
            foreach (var parent in parents)
            {
                assignAction(parent, []);
            }

            return;
        }

        // Build expression: child => parentIds.Contains(child.ForeignKeyProperty)
        var parameter = childForeignKeySelector.Parameters[0];
        var body = Expression.Call(
            Expression.Constant(parentIds),
            typeof(List<TChildIdentifierType>).GetMethod(nameof(List<>.Contains), [typeof(TChildIdentifierType)])!,
            childForeignKeySelector.Body
        );
        var lambda = Expression.Lambda<Func<TChildEntity, bool>>(body, parameter);

        var children = await childRepository.GetAllAsync(
            [],
            where: lambda,
            asTracking: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Group children by their FK value for O(1) lookup per parent
        var compiledSelector = GetOrCompileExpression(childForeignKeySelector);
        var childrenLookup = children
            .GroupBy(child => compiledSelector(child))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var parent in parents)
        {
            var key = parentKeySelector(parent);
            if (key is not null)
                assignAction(parent, childrenLookup.TryGetValue(key.Value, out var childList) ? childList : []);
            else
                assignAction(parent, []);
        }
    }

    /// <summary>
    /// Batch-loads child collection navigation properties for a collection of parent entities.
    /// Collects distinct parent IDs, executes a single <c>WHERE ParentFK IN (...)</c> query,
    /// then assigns the grouped results back to each parent.
    /// </summary>
    /// <typeparam name="TEntity">The parent entity type.</typeparam>
    /// <typeparam name="TParentIdentifierType">The parent's primary key type.</typeparam>
    /// <typeparam name="TChildEntity">The child entity type.</typeparam>
    /// <typeparam name="TChildIdentifierType">The child entity's primary key type.</typeparam>
    /// <param name="parents">The parent entities to populate.</param>
    /// <param name="parentKeySelector">Extracts the primary key from each parent.</param>
    /// <param name="childForeignKeySelector">Expression selecting the parent FK property on the child entity.</param>
    /// <param name="childRepository">Repository to query the child entities from.</param>
    /// <param name="assignAction">Callback to assign the loaded children to each parent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when all parents have been populated.</returns>
    public static async Task LoadChildrenPropertyAsync<TEntity, TParentIdentifierType, TChildEntity, TChildIdentifierType>(
        IReadOnlyCollection<TEntity> parents,
        Func<TEntity, TParentIdentifierType> parentKeySelector,
        Expression<Func<TChildEntity, TParentIdentifierType>> childForeignKeySelector,
        IReadRepository<TChildEntity, TChildIdentifierType> childRepository,
        Action<TEntity, List<TChildEntity>> assignAction,
        CancellationToken cancellationToken)
        where TParentIdentifierType : notnull
        where TChildEntity : AuditableBaseEntity<TChildIdentifierType>
        where TChildIdentifierType : notnull
    {
        var parentIds = parents
            .Select(parentKeySelector)
            .Distinct()
            .ToList();

        if (parentIds.Count == 0)
        {
            foreach (var parent in parents)
            {
                assignAction(parent, []);
            }

            return;
        }

        // Build expression: child => parentIds.Contains(child.ParentForeignKey)
        var parameter = childForeignKeySelector.Parameters[0];
        var body = Expression.Call(
            Expression.Constant(parentIds),
            typeof(List<TParentIdentifierType>).GetMethod(nameof(List<>.Contains), [typeof(TParentIdentifierType)])!,
            childForeignKeySelector.Body
        );
        var lambda = Expression.Lambda<Func<TChildEntity, bool>>(body, parameter);

        var children = await childRepository.GetAllAsync(
            [],
            where: lambda,
            asTracking: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Group children by their parent FK for O(1) lookup per parent
        var compiledSelector = GetOrCompileExpression(childForeignKeySelector);
        var childrenLookup = children
            .GroupBy(child => compiledSelector(child))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var parent in parents)
        {
            assignAction(parent, childrenLookup.TryGetValue(parentKeySelector(parent), out var childList) ? childList : []);
        }
    }

    /// <summary>
    /// Compiles and caches an expression tree delegate, keyed by source type and member access chain.
    /// Avoids repeated expression compilation calls for the same expression.
    /// </summary>
    private static Func<TSource, TResult> GetOrCompileExpression<TSource, TResult>(Expression<Func<TSource, TResult>> expression)
    {
        var cacheKey = $"{typeof(TSource).FullName}:{GetMemberPath(expression.Body)}";
        return (Func<TSource, TResult>)CompiledExpressionCache.GetOrAdd(
            cacheKey,
            _ => expression.Compile());
    }

    /// <summary>
    /// Extracts a deterministic member access path from an expression body, independent of
    /// the parameter variable name. For example, both <c>x => x.Category.Name</c> and
    /// <c>e => e.Category.Name</c> produce <c>"Category.Name"</c>.
    /// </summary>
    private static string GetMemberPath(Expression expression)
    {
        var parts = new List<string>();
        var current = expression;

        while (current is MemberExpression member)
        {
            parts.Add(member.Member.Name);
            current = member.Expression;
        }

        if (parts.Count > 0)
        {
            parts.Reverse();
            return string.Join('.', parts);
        }

        // Fallback for non-member expressions (unlikely in this codebase)
        return expression.ToString();
    }
}
