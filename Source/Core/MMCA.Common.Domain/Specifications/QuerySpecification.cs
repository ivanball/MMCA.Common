using System.Linq.Expressions;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Specifications;

/// <summary>
/// A <see cref="Specification{TEntity, TIdentifierType}"/> that carries the rest of a read's shape
/// alongside its predicate: eager-load paths, ordering, paging, tracking, and whether soft-deleted
/// rows are in scope. A repository can then serve the whole query from one object
/// (<c>ListAsync(spec)</c>) instead of the caller threading five loose arguments through every layer.
/// <para>
/// State is exposed read-only and assembled through the protected builder methods below, which
/// derived specifications call from their constructor:
/// </para>
/// <code>
/// public sealed class RecentOpenTicketsSpecification : QuerySpecification&lt;Ticket, int&gt;
/// {
///     public RecentOpenTicketsSpecification(int take)
///     {
///         AddInclude(nameof(Ticket.Comments));
///         AddOrderBy(t =&gt; t.CreatedOn, descending: true);
///         AddOrderBy(t =&gt; t.Id);
///         ApplyPaging(skip: 0, take: take);
///     }
///
///     public override Expression&lt;Func&lt;Ticket, bool&gt;&gt; Criteria =&gt; t =&gt; !t.IsClosed;
/// }
/// </code>
/// <para>
/// The base chain is deliberately <c>QuerySpecification -&gt; Specification</c>: the
/// <c>SpecificationsDoNotNavigateToOtherEntities</c> fitness rule keys on that base-type prefix and
/// on a property literally named <c>Criteria</c>, so a query specification is analyzed by exactly
/// the same rule as a plain one.
/// </para>
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public abstract class QuerySpecification<TEntity, TIdentifierType>
    : Specification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private readonly List<OrderExpression> _orderBy = [];
    private readonly List<string> _includePaths = [];

    /// <summary>Initializes a new instance with no includes, ordering, paging, or tracking.</summary>
    protected QuerySpecification() { }

    /// <summary>
    /// Gets the ordering keys in application order: the first entry becomes <c>OrderBy</c> /
    /// <c>OrderByDescending</c> and every later entry a <c>ThenBy</c> / <c>ThenByDescending</c>.
    /// Empty when the specification does not order.
    /// </summary>
    public IReadOnlyList<OrderExpression> OrderBy => _orderBy;

    /// <summary>
    /// Gets the navigation paths to eager-load, as dot-separated strings (e.g. <c>"Order.Lines"</c>).
    /// Empty when the specification loads no navigations.
    /// </summary>
    public IReadOnlyList<string> IncludePaths => _includePaths;

    /// <summary>Gets the number of rows to skip, or <see langword="null"/> when the specification does not page.</summary>
    public int? Skip { get; private set; }

    /// <summary>Gets the maximum number of rows to return, or <see langword="null"/> when the specification does not page.</summary>
    public int? Take { get; private set; }

    /// <summary>
    /// Gets whether the results should be tracked by the change tracker. Defaults to
    /// <see langword="false"/>: a specification-driven read is a read.
    /// </summary>
    public bool AsTracking { get; private set; }

    /// <summary>
    /// Gets whether soft-deleted rows are in scope. Defaults to <see langword="false"/>.
    /// <para>
    /// When <see langword="true"/> the repository drops the named <c>SoftDelete</c> global query
    /// filter and <b>only</b> that one: the <c>Tenant</c> filter stays in force, so a specification
    /// asking for deleted rows can never reach another tenant's data.
    /// </para>
    /// </summary>
    public bool IgnoreQueryFilters { get; private set; }

    /// <summary>
    /// Adds an ordering key. Call once per key, in the order they should be applied.
    /// </summary>
    /// <typeparam name="TKey">The key type the selector returns.</typeparam>
    /// <param name="keySelector">The key selector (e.g. <c>t =&gt; t.CreatedOn</c>).</param>
    /// <param name="descending">Whether this key sorts descending.</param>
    protected void AddOrderBy<TKey>(Expression<Func<TEntity, TKey>> keySelector, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        _orderBy.Add(new OrderExpression(keySelector, descending));
    }

    /// <summary>
    /// Adds a navigation path to eager-load. Blank paths are ignored; a path already added is not
    /// added twice.
    /// </summary>
    /// <param name="path">The dot-separated navigation path (e.g. <c>"Order.Lines"</c>).</param>
    protected void AddInclude(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!_includePaths.Contains(path, StringComparer.Ordinal))
            _includePaths.Add(path);
    }

    /// <summary>
    /// Sets the paging window. Both values are floored at zero, so a negative offset or size
    /// degrades to "from the start" / "no rows" instead of throwing inside the provider.
    /// </summary>
    /// <param name="skip">The number of rows to skip.</param>
    /// <param name="take">The maximum number of rows to return.</param>
    protected void ApplyPaging(int skip, int take)
    {
        Skip = Math.Max(skip, 0);
        Take = Math.Max(take, 0);
    }

    /// <summary>
    /// Opts the results into change tracking. Use only when the caller mutates and saves what it
    /// reads; every other read should stay untracked.
    /// </summary>
    protected void WithTracking() => AsTracking = true;

    /// <summary>
    /// Brings soft-deleted rows into scope (an admin restore screen, a data-subject export). It
    /// drops the named <c>SoftDelete</c> filter and only that one: tenant scoping still applies.
    /// </summary>
    protected void WithSoftDeleted() => IgnoreQueryFilters = true;

}

/// <summary>
/// One ordering key of a <see cref="QuerySpecification{TEntity, TIdentifierType}"/>.
/// </summary>
/// <remarks>
/// It is a top-level type rather than a member of the generic specification on purpose: a nested
/// type of a generic class is a different type per closed generic, which would stop the repository
/// evaluator from handling an ordering list generically.
/// </remarks>
/// <param name="KeySelector">
/// The key selector, kept as a <see cref="LambdaExpression"/> so keys of different types can share
/// one list; the evaluator binds it back to its concrete key type by reflection.
/// </param>
/// <param name="Descending">Whether this key sorts descending.</param>
public sealed record OrderExpression(LambdaExpression KeySelector, bool Descending);
