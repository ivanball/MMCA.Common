using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
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

    /// <summary>
    /// The single global filter <c>ignoreQueryFilters: true</c> is allowed to drop. EF 10 names
    /// filters, and the framework registers two: <c>SoftDelete</c> and <c>Tenant</c>. Dropping both
    /// (EF's parameterless <c>IgnoreQueryFilters()</c>) would let a caller asking to see deleted rows
    /// silently read every tenant's data, so the repository names the one it means.
    /// </summary>
    private static readonly string[] SoftDeleteFilterOnly = [DbContexts.ApplicationDbContext.SoftDeleteFilterName];

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
            query = query.IgnoreQueryFilters(SoftDeleteFilterOnly);

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
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(select);

        var query = asTracking ? Table : TableNoTracking;

        if (ignoreQueryFilters)
            query = query.IgnoreQueryFilters(SoftDeleteFilterOnly);

        if (where is not null)
            query = query.Where(where);

        return await query.Select(select).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> where,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);

        var query = asTracking ? Table : TableNoTracking;

        if (ignoreQueryFilters)
            query = query.IgnoreQueryFilters(SoftDeleteFilterOnly);

        if (includes is not null)
            query = ApplyIncludes(query, includes);

        // TOP 1 at the database, not a materialized set narrowed in memory afterwards.
        return await query.FirstOrDefaultAsync(where, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        // The full shape, ordering included: "first" is only meaningful against a defined order, and
        // the specification is where that order is declared.
        return await SpecificationEvaluator
            .Apply<TEntity, TIdentifierType>(BaseQueryFor(specification), specification)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<TKey, int>> CountByAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? where = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        var query = TableNoTracking;

        if (where is not null)
            query = query.Where(where);

        var groups = await query
            .GroupBy(keySelector)
            .Select(group => new GroupedCount<TKey>(group.Key, group.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return groups.ToDictionary(group => group.Key, group => group.Value);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<TKey, decimal>> SumByAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, decimal>> sumSelector,
        Expression<Func<TEntity, bool>>? where = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(sumSelector);

        var query = TableNoTracking;

        if (where is not null)
            query = query.Where(where);

        // GroupBy with an ELEMENT selector: the summed column is chosen inside the grouping, so the
        // caller's expression tree reaches the provider intact. Summing over the grouping with a
        // separately supplied expression would need the tree spliced in by hand.
        var groups = await query
            .GroupBy(keySelector, sumSelector)
            .Select(group => new GroupedSum<TKey>(group.Key, group.Sum()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return groups.ToDictionary(group => group.Key, group => group.Value);
    }

    /// <summary>One <c>GROUP BY</c> row of <see cref="CountByAsync{TKey}"/>.</summary>
    /// <param name="Key">The grouping key.</param>
    /// <param name="Value">The number of rows carrying that key.</param>
    private sealed record class GroupedCount<TKey>(TKey Key, int Value);

    /// <summary>One <c>GROUP BY</c> row of <see cref="SumByAsync{TKey}"/>.</summary>
    /// <param name="Key">The grouping key.</param>
    /// <param name="Value">The summed value for that key.</param>
    private sealed record class GroupedSum<TKey>(TKey Key, decimal Value);

    /// <inheritdoc />
    public virtual async Task<(IReadOnlyCollection<TEntity> Active, IReadOnlyCollection<TEntity> SoftDeleted)> FindIncludingDeletedAsync(
        Expression<Func<TEntity, bool>> where,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);

        // One round trip with the soft-delete filter dropped, split afterwards: two queries would
        // let a concurrent delete land between them and report the same row in neither half.
        var query = (asTracking ? Table : TableNoTracking).IgnoreQueryFilters(SoftDeleteFilterOnly);

        if (includes is not null)
            query = ApplyIncludes(query, includes);

        var rows = await query.Where(where).ToListAsync(cancellationToken).ConfigureAwait(false);

        List<TEntity> active = [];
        List<TEntity> softDeleted = [];

        foreach (var row in rows)
        {
            (row.IsDeleted ? softDeleted : active).Add(row);
        }

        return (active, softDeleted);
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
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids as IReadOnlyCollection<TIdentifierType> ?? [.. ids];
        if (idList.Count == 0)
            return [];

        var query = asTracking ? Table : TableNoTracking;

        if (ignoreQueryFilters)
            query = query.IgnoreQueryFilters(SoftDeleteFilterOnly);

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

        // A filtered query, not FindAsync: FindAsync serves a tracked instance straight from the
        // identity map without evaluating the global soft-delete filter, so an entity soft-deleted
        // earlier in the same scope came back as if it were live. Table (tracked) is deliberate:
        // EFRepository inherits this member and the generic delete/update handlers load through it,
        // mutate, and save, so a no-tracking query would turn those into silent no-ops.
        return await Table.FirstOrDefaultAsync(e => e.Id.Equals(id), cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public virtual async Task<int> CountAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return await SpecificationEvaluator
            .Apply<TEntity, TIdentifierType>(BaseQueryFor(specification), specification, applyShape: false)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether an entity with the given ID exists.
    /// </summary>
    public virtual async Task<bool> ExistsAsync(
        TIdentifierType id,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return await AnyAsync(
            ignoreQueryFilters ? Entities.IgnoreQueryFilters(SoftDeleteFilterOnly) : Entities,
            e => e.Id.Equals(id),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);

        return await AnyAsync(
            ignoreQueryFilters ? Entities.IgnoreQueryFilters(SoftDeleteFilterOnly) : Entities,
            where,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Existence check, provider-aware.
    /// </summary>
    /// <remarks>
    /// Cosmos DB needs <c>CountAsync</c>: its provider generates invalid SQL (unresolved 'root'
    /// identifier) when translating a predicated <c>AnyAsync</c> into a subquery. Every other
    /// provider gets <c>AnyAsync</c>, which short-circuits at the first match; <c>CountAsync</c>
    /// reads every matching row, so on a predicate that matches a wide set the workaround cost
    /// O(matches) on providers that never needed it.
    /// </remarks>
    private async Task<bool> AnyAsync(
        IQueryable<TEntity> query,
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken)
        => IsCosmosProvider
            ? await query.CountAsync(where, cancellationToken).ConfigureAwait(false) > 0
            : await query.AnyAsync(where, cancellationToken).ConfigureAwait(false);

    private bool IsCosmosProvider =>
        _context.Database.ProviderName?.Contains("Cosmos", StringComparison.Ordinal) == true;

    /// <summary>Gets a tracked queryable over the entity set.</summary>
    public virtual IQueryable<TEntity> Table => Entities;

    /// <summary>Gets a no-tracking queryable — use for read-only queries to avoid change-tracker overhead.</summary>
    public virtual IQueryable<TEntity> TableNoTracking => Entities.AsNoTracking();

    /// <summary>Gets a no-tracking queryable that loads all includes in a single SQL query.</summary>
    public virtual IQueryable<TEntity> TableNoTrackingSingleQuery => TableNoTracking.AsSingleQuery();

    /// <summary>Gets a no-tracking queryable that loads includes via separate SQL queries to avoid cartesian explosion.</summary>
    public virtual IQueryable<TEntity> TableNoTrackingSplitQuery => TableNoTracking.AsSplitQuery();

    /// <summary>
    /// Applies string-based eager loading includes to the query, including the collection-navigation
    /// split-query auto-switch. The logic itself lives once in
    /// <see cref="SpecificationEvaluator"/>, which the specification path uses as well, so the two
    /// entry points can never drift apart.
    /// </summary>
    /// <param name="query">The queryable to apply the includes to.</param>
    /// <param name="includes">The dot-separated navigation paths.</param>
    /// <returns>The queryable with the includes applied.</returns>
    protected static IQueryable<TEntity> ApplyIncludes(
        IQueryable<TEntity> query,
        IEnumerable<string> includes)
        => SpecificationEvaluator.ApplyIncludes(query, includes);

    // ── Specification-driven reads ───────────────────────────────────────────────────────────

    /// <summary>
    /// Chooses the base queryable a specification runs on: tracked or not, with or without the named
    /// soft-delete filter dropped. Only a
    /// <see cref="QuerySpecification{TEntity, TIdentifierType}"/> carries those choices; a plain
    /// specification gets the untracked, filtered default.
    /// </summary>
    private IQueryable<TEntity> BaseQueryFor(ISpecification<TEntity, TIdentifierType> specification)
    {
        var querySpecification = specification as QuerySpecification<TEntity, TIdentifierType>;

        var query = querySpecification?.AsTracking == true ? Table : TableNoTracking;

        return querySpecification?.IgnoreQueryFilters == true
            ? query.IgnoreQueryFilters(SoftDeleteFilterOnly)
            : query;
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<TEntity>> ListAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        return await SpecificationEvaluator
            .Apply<TEntity, TIdentifierType>(BaseQueryFor(specification), specification)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyCollection<TResult>> ListAsync<TResult>(
        ISpecification<TEntity, TIdentifierType> specification,
        Expression<Func<TEntity, TResult>> select,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(select);

        // Select last: ordering and paging must run over entity rows, so a paged specification pages
        // the rows it means to page and only that page is projected.
        return await SpecificationEvaluator
            .Apply<TEntity, TIdentifierType>(BaseQueryFor(specification), specification)
            .Select(select)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<bool> AnyAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        // Criteria only, and through the same Cosmos-aware existence check the predicate overloads use.
        return await AnyAsync(
            BaseQueryFor(specification),
            specification.Criteria,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<Result<KeysetCollectionResult<TEntity>>> GetPageByCursorAsync(
        KeysetPageRequest request,
        ISpecification<TEntity, TIdentifierType>? specification = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!KeysetQueryBuilder.TryResolveSortProperty<TEntity>(request.SortColumn, out var sortProperty))
        {
            return Result.Failure<KeysetCollectionResult<TEntity>>(
                Error.InvalidEntityField with
                {
                    Message = $"Sort column '{request.SortColumn}' does not exist on type '{typeof(TEntity).Name}'.",
                    Source = nameof(GetPageByCursorAsync),
                    Target = typeof(TEntity).Name,
                });
        }

        var query = specification is null
            ? TableNoTracking
            : SpecificationEvaluator.Apply<TEntity, TIdentifierType>(BaseQueryFor(specification), specification, applyShape: false);

        if (request.Cursor is not null)
        {
            if (!TryBuildSeekPredicate(request, sortProperty, out var seek))
            {
                return Result.Failure<KeysetCollectionResult<TEntity>>(
                    Error.Validation(
                        "Error.InvalidCursor",
                        "The supplied pagination cursor isn't valid.",
                        nameof(GetPageByCursorAsync),
                        typeof(TEntity).Name));
            }

            query = query.Where(seek);
        }

        query = KeysetQueryBuilder.ApplyOrdering<TEntity, TIdentifierType>(query, sortProperty, request.Descending);

        // One extra row is the next-page probe: it is never returned, it only says whether a next
        // page exists, which is cheaper and more honest than a COUNT over the whole set.
        var rows = await query.Take(request.PageSize + 1).ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > request.PageSize;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        string? nextCursor = null;
        if (hasMore && rows.Count != 0)
        {
            var last = rows[^1];
            nextCursor = KeysetCursor.Encode(
                KeysetQueryBuilder.ToInvariantString(sortProperty?.GetValue(last)),
                KeysetQueryBuilder.ToInvariantString(last.Id) ?? string.Empty);
        }

        return Result.Success(new KeysetCollectionResult<TEntity>(rows, nextCursor));
    }

    /// <summary>
    /// Decodes the request's cursor and turns it into the seek predicate, or reports that the cursor
    /// is malformed (bad encoding, wrong version, or values that do not parse as this entity's key
    /// and sort types).
    /// </summary>
    private static bool TryBuildSeekPredicate(
        KeysetPageRequest request,
        PropertyInfo? sortProperty,
        out Expression<Func<TEntity, bool>> seek)
    {
        seek = null!;

        if (!KeysetCursor.TryDecode(request.Cursor, out var sortText, out var idText))
            return false;

        if (!KeysetQueryBuilder.TryFromInvariantString(typeof(TIdentifierType), idText, out var id)
            || id is not TIdentifierType typedId)
        {
            return false;
        }

        object? sortValue = null;
        if (sortProperty is not null
            && sortText is not null
            && !KeysetQueryBuilder.TryFromInvariantString(sortProperty.PropertyType, sortText, out sortValue))
        {
            return false;
        }

        seek = KeysetQueryBuilder.BuildSeekPredicate<TEntity, TIdentifierType>(
            sortProperty, sortValue, typedId, request.Descending);

        return true;
    }
}
