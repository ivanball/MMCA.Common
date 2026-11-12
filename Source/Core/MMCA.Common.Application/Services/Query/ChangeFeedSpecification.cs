using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// The read scope of the change feed: rows changed at or after a UTC instant, ANDed with the caller's
/// scope, with the soft-delete filter lifted so a deleted row can be reported as a tombstone. Every
/// other global filter (the tenant filter included) stays in force.
/// </summary>
/// <typeparam name="TEntity">The audited entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
internal sealed class ChangeFeedSpecification<TEntity, TIdentifierType> : QuerySpecification<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeFeedSpecification{TEntity, TIdentifierType}"/> class.
    /// </summary>
    /// <param name="sinceUtc">The inclusive UTC lower bound on <c>LastModifiedOn</c>, or null for no time bound.</param>
    /// <param name="scope">
    /// The caller's scope, or null. Only its <c>Criteria</c> is used, wrapped before composition, so a
    /// scope that is itself a shaped <see cref="QuerySpecification{TEntity, TIdentifierType}"/> is accepted.
    /// </param>
    public ChangeFeedSpecification(DateTime? sinceUtc, ISpecification<TEntity, TIdentifierType>? scope)
    {
        Criteria = BuildCriteria(sinceUtc, scope);
        WithSoftDeleted();
    }

    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria { get; }

    private static Expression<Func<TEntity, bool>> BuildCriteria(DateTime? sinceUtc, ISpecification<TEntity, TIdentifierType>? scope)
    {
        Specification<TEntity, TIdentifierType>? timeBound = null;
        if (sinceUtc is { } bound)
        {
            timeBound = new InlineSpecification<TEntity, TIdentifierType>(e => e.LastModifiedOn >= bound);
        }

        var wrappedScope = scope is null
            ? null
            : new InlineSpecification<TEntity, TIdentifierType>(scope.Criteria);

        return (timeBound, wrappedScope) switch
        {
            ({ } time, { } scoped) => new AndSpecification<TEntity, TIdentifierType>(time, scoped).Criteria,
            ({ } time, null) => time.Criteria,
            (null, { } scoped) => scoped.Criteria,
            _ => _ => true,
        };
    }
}
