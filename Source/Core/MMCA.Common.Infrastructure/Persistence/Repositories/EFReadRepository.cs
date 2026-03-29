using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core read-only repository providing query operations (get, count, exists) without mutation support.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal class EFReadRepository<TEntity, TIdentifierType>(
    DbContext context
) : IReadRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    protected readonly DbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    protected virtual DbSet<TEntity> Entities => _context.Set<TEntity>();

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<TEntity>> GetAllAsync(
        IEnumerable<string> includes,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        Expression<Func<TEntity, TEntity>>? select = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        var query = asTracking
            ? Table
            : TableNoTracking;

        if (ignoreQueryFilters)
            query = query.IgnoreQueryFilters();

        query = ApplyIncludes(query, includes);

        if (where is not null)
            query = query.Where(where);

        if (orderBy is not null)
            query = query.OrderBy(orderBy);

        if (select is not null)
            return await query.Select(select).ToListAsync(cancellationToken).ConfigureAwait(false);

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<TResult>> GetProjectedAsync<TResult>(
        Expression<Func<TEntity, TResult>> select,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(select);

        var query = asTracking ? Table : TableNoTracking;

        if (where is not null)
            query = query.Where(where);

        return await query.Select(select).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        var query = asTracking ? Table : TableNoTracking;

        if (where is not null)
            query = query.Where(where);

        var selector = GetOrBuildLookupSelector(nameProperty);

        return await query
            .Select(selector)
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Caches compiled expression trees per name property so repeated lookup queries
    /// avoid the overhead of building the projection expression each time.
    /// Keyed by property name; safe across concurrent requests via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type EntityType, string PropertyName), LambdaExpression> LookupSelectorCache = new();

    /// <summary>
    /// Gets or builds a projection expression mapping the entity's Id and the named property to <see cref="BaseLookup{TIdentifierType}"/>.
    /// </summary>
    private static Expression<Func<TEntity, BaseLookup<TIdentifierType>>> GetOrBuildLookupSelector(string nameProperty) =>
        (Expression<Func<TEntity, BaseLookup<TIdentifierType>>>)LookupSelectorCache.GetOrAdd(
            (typeof(TEntity), nameProperty),
            static key =>
            {
                var param = Expression.Parameter(typeof(TEntity), "e");
                var idAccess = Expression.Property(param, "Id");
                var nameAccess = Expression.Property(param, key.PropertyName);

                Expression nameExpr = nameAccess.Type == typeof(string)
                    ? Expression.Coalesce(nameAccess, Expression.Constant(string.Empty))
                    : Expression.Call(
                        nameAccess,
                        nameAccess.Type.GetMethod("ToString", Type.EmptyTypes)!);

                var lookupType = typeof(BaseLookup<TIdentifierType>);
                var body = Expression.MemberInit(
                    Expression.New(lookupType),
                    Expression.Bind(lookupType.GetProperty(nameof(BaseLookup<>.Id))!, idAccess),
                    Expression.Bind(lookupType.GetProperty(nameof(BaseLookup<>.Name))!, nameExpr));

                return Expression.Lambda<Func<TEntity, BaseLookup<TIdentifierType>>>(body, param);
            });

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<TEntity>> GetByIdsAsync(
        IEnumerable<TIdentifierType> ids,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids as IReadOnlyCollection<TIdentifierType> ?? [.. ids];
        if (idList.Count == 0)
            return [];

        var query = asTracking ? Table : TableNoTracking;

        if (includes is not null)
            query = ApplyIncludes(query, includes);

        return await query.Where(e => idList.Contains(e.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return await Entities.FindAsync([id], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        IEnumerable<string> includes,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(includes);

        var query = asTracking ? Table : TableNoTracking;
        query = ApplyIncludes(query, includes);

        return await query.FirstOrDefaultAsync(e => e.Id.Equals(id), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<int> CountAsync(CancellationToken cancellationToken = default)
        => await Entities.CountAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public virtual async Task<int> CountAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);

        return await Entities.CountAsync(where, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether an entity with the given ID exists.
    /// </summary>
    /// <remarks>
    /// Uses <c>CountAsync</c> instead of <c>AnyAsync</c> as a workaround for a Cosmos DB provider bug
    /// that generates invalid SQL (unresolved 'root' identifier) when translating <c>AnyAsync</c>
    /// with a predicate into a subquery.
    /// </remarks>
    public virtual async Task<bool> ExistsAsync(
        TIdentifierType id,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (ignoreQueryFilters)
        {
            return await Entities
                .IgnoreQueryFilters()
                .CountAsync(e => e.Id.Equals(id), cancellationToken)
                .ConfigureAwait(false) > 0;
        }

        return await Entities.CountAsync(e => e.Id.Equals(id), cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public virtual async Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);

        if (ignoreQueryFilters)
        {
            return await Entities
                .IgnoreQueryFilters()
                .CountAsync(where, cancellationToken)
                .ConfigureAwait(false) > 0;
        }

        return await Entities.CountAsync(where, cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>Gets a tracked queryable over the entity set.</summary>
    public virtual IQueryable<TEntity> Table => Entities;

    /// <summary>Gets a no-tracking queryable — use for read-only queries to avoid change-tracker overhead.</summary>
    public virtual IQueryable<TEntity> TableNoTracking => Entities.AsNoTracking();

    /// <summary>Gets a no-tracking queryable that loads all includes in a single SQL query.</summary>
    public virtual IQueryable<TEntity> TableNoTrackingSingleQuery => TableNoTracking.AsSingleQuery();

    /// <summary>Gets a no-tracking queryable that loads includes via separate SQL queries to avoid cartesian explosion.</summary>
    public virtual IQueryable<TEntity> TableNoTrackingSplitQuery => TableNoTracking.AsSplitQuery();

    /// <summary>
    /// Applies string-based eager loading includes to the query. Skips empty/whitespace entries.
    /// </summary>
    protected static IQueryable<TEntity> ApplyIncludes(
        IQueryable<TEntity> query,
        IEnumerable<string> includes)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(includes);

        foreach (string include in includes.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            query = query.Include(include);
        }

        return query;
    }
}
