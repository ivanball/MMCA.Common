using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Focused interface for single-entity lookups by ID.
/// Prefer this over <see cref="IReadRepository{TEntity,TIdentifierType}"/> when a handler
/// only needs <c>GetByIdAsync</c> or <c>ExistsAsync</c> — this signals minimal data access.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <remarks>
/// Every <c>ignoreQueryFilters</c> parameter on this interface means "include soft-deleted rows",
/// nothing more. It drops the named <c>SoftDelete</c> filter and leaves the named <c>Tenant</c>
/// filter applied.
/// </remarks>
public interface IEntityReader<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves a single entity by its primary key.</summary>
    Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves a single entity by its primary key with navigation properties eagerly loaded.</summary>
    Task<TEntity?> GetByIdAsync(
        TIdentifierType id,
        IEnumerable<string> includes,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves multiple entities by their primary keys in a single query.</summary>
    /// <param name="ids">The collection of primary keys to look up.</param>
    /// <param name="includes">Navigation properties to eager-load.</param>
    /// <param name="asTracking">Whether to track the returned entities for changes.</param>
    /// <param name="ignoreQueryFilters">
    /// Whether to include soft-deleted rows. It drops the <c>SoftDelete</c> global query filter and
    /// <b>only</b> that one: the <c>Tenant</c> filter stays in force, so a caller asking for deleted
    /// rows can never read another tenant's data.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only collection of matching entities (may be fewer than requested if some IDs don't exist).</returns>
    Task<IReadOnlyCollection<TEntity>> GetByIdsAsync(
        IEnumerable<TIdentifierType> ids,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether an entity with the given id exists.</summary>
    Task<bool> ExistsAsync(
        TIdentifierType id,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether any entity matches the predicate.</summary>
    Task<bool> ExistsAsync(
        Expression<Func<TEntity, bool>> where,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Focused interface for collection queries, projections, and counting.
/// Prefer this over <see cref="IReadRepository{TEntity,TIdentifierType}"/> when a handler
/// needs <c>GetAllAsync</c>, <c>GetProjectedAsync</c>, or <c>CountAsync</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <remarks>
/// Every <c>ignoreQueryFilters</c> parameter on this interface means "include soft-deleted rows",
/// nothing more. It drops the named <c>SoftDelete</c> filter and leaves the named <c>Tenant</c>
/// filter applied.
/// </remarks>
public interface IEntityQuerier<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Retrieves all entities matching optional includes, filter, ordering, and projection.</summary>
    Task<IReadOnlyCollection<TEntity>> GetAllAsync(
        IEnumerable<string> includes,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        Expression<Func<TEntity, TEntity>>? select = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves entities projected to a different type via a selector expression (translated to SQL).</summary>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="select">The projection expression.</param>
    /// <param name="where">Optional filter predicate.</param>
    /// <param name="asTracking">Whether to track the source entities for changes.</param>
    /// <param name="ignoreQueryFilters">
    /// Whether to include soft-deleted rows. It drops the <c>SoftDelete</c> global query filter and
    /// <b>only</b> that one: the <c>Tenant</c> filter stays in force, so a caller asking for deleted
    /// rows can never read another tenant's data.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyCollection<TResult>> GetProjectedAsync<TResult>(
        Expression<Func<TEntity, TResult>> select,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the FIRST entity matching a predicate, or <see langword="null"/> when none does.
    /// </summary>
    /// <remarks>
    /// The point is that the database returns one row. The alternative a caller reaches for without
    /// this member is <c>GetAllAsync</c> followed by an in-memory <c>FirstOrDefault</c>, which
    /// materializes the whole matching set to keep one entity. No ordering is applied, so the "first"
    /// row is whatever the provider returns first: use
    /// <see cref="FirstOrDefaultAsync(ISpecification{TEntity, TIdentifierType}, CancellationToken)"/>
    /// when the choice among several matches has to be deterministic.
    /// </remarks>
    /// <param name="where">The filter predicate.</param>
    /// <param name="includes">Navigation properties to eager-load.</param>
    /// <param name="asTracking">Whether to track the returned entity for changes.</param>
    /// <param name="ignoreQueryFilters">
    /// Whether to include soft-deleted rows. It drops the <c>SoftDelete</c> global query filter and
    /// <b>only</b> that one: the <c>Tenant</c> filter stays in force, so a caller asking for deleted
    /// rows can never read another tenant's data.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first matching entity, or <see langword="null"/>.</returns>
    Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> where,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the first entity a specification matches, or <see langword="null"/> when none does.
    /// Unlike the predicate overload this honors the specification's ORDERING, so "first" means what
    /// the specification says it means.
    /// </summary>
    /// <param name="specification">The specification describing the read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first matching entity, or <see langword="null"/>.</returns>
    Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the matching rows PER KEY in the database (a <c>GROUP BY</c>), returning one entry per
    /// key that has at least one row.
    /// </summary>
    /// <remarks>
    /// The Application layer references no EF Core, so a handler that needs a grouped count has no
    /// <c>IQueryable</c> to group and folds the rows in memory instead: it projects every matching
    /// row out of the database and groups them client-side. This member is the persistence-neutral
    /// way to ask the database the same question, so only the aggregate crosses the wire.
    /// </remarks>
    /// <typeparam name="TKey">The grouping key type (must be translatable by the provider).</typeparam>
    /// <param name="keySelector">Selects the value to group by.</param>
    /// <param name="where">Optional filter applied before grouping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary of key to row count; keys with no matching rows are absent.</returns>
    Task<IReadOnlyDictionary<TKey, int>> CountByAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? where = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Sums a value PER KEY in the database (a <c>GROUP BY</c> with <c>SUM</c>), returning one entry
    /// per key that has at least one row. The grouped counterpart of
    /// <see cref="CountByAsync{TKey}"/>.
    /// </summary>
    /// <typeparam name="TKey">The grouping key type (must be translatable by the provider).</typeparam>
    /// <param name="keySelector">Selects the value to group by.</param>
    /// <param name="sumSelector">Selects the value to sum within each group.</param>
    /// <param name="where">Optional filter applied before grouping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary of key to summed value; keys with no matching rows are absent.</returns>
    Task<IReadOnlyDictionary<TKey, decimal>> SumByAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, decimal>> sumSelector,
        Expression<Func<TEntity, bool>>? where = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Reads the rows matching a predicate INCLUDING soft-deleted ones, split into the active rows
    /// and the soft-deleted rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the resurrection read (BR-135): a create handler whose natural key already exists as a
    /// soft-deleted row must reactivate that row rather than insert a duplicate the unique index will
    /// reject, and it needs both halves of the answer in one round trip: an active match is a
    /// conflict, a soft-deleted match is the row to bring back, neither is a plain insert.
    /// </para>
    /// <para>
    /// As everywhere on this interface, dropping the query filter means "include soft-deleted rows"
    /// and nothing more: the <c>Tenant</c> filter stays in force.
    /// </para>
    /// </remarks>
    /// <param name="where">The filter predicate.</param>
    /// <param name="includes">Navigation properties to eager-load.</param>
    /// <param name="asTracking">
    /// Whether to track the returned entities. A caller that intends to reactivate a soft-deleted row
    /// wants <see langword="true"/>, otherwise the reactivation saves nothing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching rows, partitioned into active and soft-deleted.</returns>
    Task<(IReadOnlyCollection<TEntity> Active, IReadOnlyCollection<TEntity> SoftDeleted)> FindIncludingDeletedAsync(
        Expression<Func<TEntity, bool>> where,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Retrieves entities as lightweight id/name pairs for lookup scenarios.</summary>
    Task<IReadOnlyCollection<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the total count of entities.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the count of entities matching the predicate.</summary>
    Task<int> CountAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the rows a specification matches. Ordering and paging on the specification are ignored
    /// deliberately: a count of "page 3 of the matches" is never what a caller means.
    /// </summary>
    /// <param name="specification">The specification describing the read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of matching rows.</returns>
    Task<int> CountAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a specification and returns the matching entities.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="ISpecification{TEntity, TIdentifierType}"/> contributes its
    /// <c>Criteria</c> only. A
    /// <see cref="Domain.Specifications.QuerySpecification{TEntity, TIdentifierType}"/> also
    /// contributes its includes, ordering, paging, tracking, and soft-delete scope, so the whole
    /// read is described by one object instead of five loose arguments.
    /// </remarks>
    /// <param name="specification">The specification describing the read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching entities.</returns>
    Task<IReadOnlyCollection<TEntity>> ListAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a specification and projects the matching entities server-side, so only the selected
    /// columns leave the database (projection pushdown).
    /// </summary>
    /// <remarks>
    /// The projection is applied AFTER the specification's ordering and paging, so a paged
    /// specification still pages over entity rows and projects only that page. Includes on the
    /// specification are redundant on this overload (the projection decides what is loaded) but not
    /// harmful.
    /// </remarks>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="specification">The specification describing the read.</param>
    /// <param name="select">The projection expression (must be translatable by the provider).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projected results.</returns>
    Task<IReadOnlyCollection<TResult>> ListAsync<TResult>(
        ISpecification<TEntity, TIdentifierType> specification,
        Expression<Func<TEntity, TResult>> select,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a specification matches any row. Ordering and paging are ignored, as for
    /// <see cref="CountAsync(ISpecification{TEntity, TIdentifierType}, CancellationToken)"/>.
    /// </summary>
    /// <param name="specification">The specification describing the read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    Task<bool> AnyAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one keyset ("seek") page: the rows strictly after the request's cursor, ordered by the
    /// requested sort column with <c>Id</c> as tie-break, plus the cursor for the next page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike offset paging this costs one index seek regardless of how deep the caller has scrolled,
    /// and it never skips or repeats a row when the underlying set changes between pages. The trade
    /// is no random page access and no total count.
    /// </para>
    /// <para>
    /// Exactly one sort key is supported. A null <see cref="KeysetPageRequest.SortColumn"/> keys the
    /// page on <c>Id</c> alone. The sort column must name a real public property of the entity: an
    /// unknown name and a malformed cursor both come back as a validation failure, never as a silent
    /// first page.
    /// </para>
    /// </remarks>
    /// <param name="request">The page size, sort key, direction, and cursor.</param>
    /// <param name="specification">Optional specification whose <c>Criteria</c> scopes the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page and its next cursor, or a validation failure.</returns>
    Task<Result<KeysetCollectionResult<TEntity>>> GetPageByCursorAsync(
        KeysetPageRequest request,
        ISpecification<TEntity, TIdentifierType>? specification = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only repository combining <see cref="IEntityReader{TEntity,TIdentifierType}"/>,
/// <see cref="IEntityQuerier{TEntity,TIdentifierType}"/>, and direct IQueryable access.
/// Existing code should continue using this interface; new handlers can depend on the
/// focused sub-interfaces for better ISP compliance.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IReadRepository<TEntity, TIdentifierType>
    : IEntityReader<TEntity, TIdentifierType>, IEntityQuerier<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Base queryable with change tracking enabled.</summary>
    IQueryable<TEntity> Table { get; }

    /// <summary>Base queryable with no-tracking (read-only, best for queries).</summary>
    IQueryable<TEntity> TableNoTracking { get; }

    /// <summary>No-tracking queryable configured for single SQL query execution.</summary>
    IQueryable<TEntity> TableNoTrackingSingleQuery { get; }

    /// <summary>No-tracking queryable configured for split query execution (avoids cartesian explosion).</summary>
    IQueryable<TEntity> TableNoTrackingSplitQuery { get; }
}

/// <summary>
/// Write repository for persisting entity changes.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IWriteRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Adds a new entity to the persistence store.</summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddAsync(
        TEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>Adds multiple entities to the persistence store in a single batch.</summary>
    /// <param name="entities">The entities to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddRangeAsync(
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an existing entity as modified for persistence.</summary>
    /// <param name="entity">The entity to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateAsync(
        TEntity entity,
        CancellationToken cancellationToken = default);

    /// <summary>Marks multiple existing entities as modified for persistence in a single batch.</summary>
    /// <param name="entities">The entities to update.</param>
    void UpdateRange(IEnumerable<TEntity> entities);

    /// <summary>
    /// Applies a client-supplied optimistic-concurrency token as the tracked entity's original
    /// <c>RowVersion</c>, so the next save raises <c>DbUpdateConcurrencyException</c> (mapped to
    /// <c>409 Conflict</c>) when the row was modified by someone else since the client read it.
    /// No-op when <paramref name="rowVersion"/> is null or empty (legacy clients / first write).
    /// </summary>
    /// <param name="entity">The tracked entity whose original concurrency token should be set.</param>
    /// <param name="rowVersion">The client's last-observed <c>RowVersion</c>, or null to skip the check.</param>
    void SetOriginalRowVersion(TEntity entity, byte[]? rowVersion);

    /// <summary>
    /// Applies a client-supplied optimistic-concurrency token to a tracked CHILD entity of this
    /// aggregate (e.g. a <c>ProductVariant</c> under a <c>Product</c>), so a child-level edit gets
    /// the same stale-token 409 protection as the aggregate root (ADR-035). The aggregate-typed
    /// overload above cannot reach children because the repository's <c>TEntity</c> is the root;
    /// this overload accepts any <see cref="Domain.Interfaces.IRowVersioned"/> entity instead.
    /// No-op when <paramref name="rowVersion"/> is null or empty (legacy clients / first write).
    /// </summary>
    /// <param name="childEntity">The tracked child entity whose original concurrency token should be set.</param>
    /// <param name="rowVersion">The client's last-observed <c>RowVersion</c>, or null to skip the check.</param>
    void SetOriginalRowVersion(Domain.Interfaces.IRowVersioned childEntity, byte[]? rowVersion);

    /// <summary>
    /// Executes a bulk delete directly in the database, bypassing change tracking.
    /// WARNING: Does NOT trigger domain events, audit stamps, or soft-delete behavior.
    /// Use only for maintenance scenarios where domain events are not needed.
    /// </summary>
    /// <param name="where">A predicate identifying the entities to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> ExecuteDeleteAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a set-based UPDATE directly in the database, bypassing change tracking:
    /// <c>UPDATE ... SET ... WHERE ...</c> as one atomic statement. The intended use is
    /// contention-proof conditional updates (e.g. a stock decrement guarded by
    /// <c>AvailableQuantity &gt;= @qty</c> in <paramref name="where"/>): zero rows affected
    /// means the condition did not hold, and two racing callers can never both win, with no
    /// rowversion retry loop, because the database itself arbitrates.
    /// <para>
    /// WARNING: bypasses domain events. Global query filters (soft delete) DO apply to
    /// <paramref name="where"/>. Audit fields are NOT bypassed: <c>LastModifiedOn</c> and
    /// <c>LastModifiedBy</c> are stamped automatically unless the caller sets them explicitly.
    /// Runs on the ambient transaction when one is active (see
    /// <c>IUnitOfWork.ExecuteInTransactionAsync</c>), so decrements roll back with the caller.
    /// </para>
    /// </summary>
    /// <param name="where">A predicate selecting (and guarding) the rows to update.</param>
    /// <param name="setProperties">Builder describing the properties to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows updated (0 = the guard condition matched no row).</returns>
    Task<int> ExecuteUpdateAsync(
        Expression<Func<TEntity, bool>> where,
        Action<IUpdatePropertySetter<TEntity>> setProperties,
        CancellationToken cancellationToken = default);

    /// <summary>Synchronous save. Prefer <see cref="SaveChangesAsync"/> in async code paths.</summary>
    /// <returns>The number of state entries written.</returns>
    int Save();

    /// <summary>Persists all pending changes asynchronously.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of state entries written.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined read-write repository interface.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IRepository<TEntity, TIdentifierType> : IReadRepository<TEntity, TIdentifierType>, IWriteRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull;
