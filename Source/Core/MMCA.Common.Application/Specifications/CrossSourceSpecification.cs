using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Application.Specifications;

/// <summary>
/// Builds a specification that filters a dependent entity by a condition on a <b>cross-source</b>
/// principal it references by foreign key. In a polyglot setup a query cannot join across physical
/// data sources (e.g. a Cosmos <c>Session</c> cannot join to a SQL Server <c>Event</c>), so a
/// predicate like <c>s =&gt; s.Event.IsPublished</c> is not translatable. This helper resolves the
/// matching principal keys first (a scalar projection query against the principal's own source) and
/// returns a specification whose criteria is the engine-portable
/// <c>localPredicate AND principalKeys.Contains(dependent.ForeignKey)</c>.
/// <para>
/// Note: the matching principal keys are materialized and embedded in the predicate, so this fits
/// principal sets that are small/bounded (the common "published events", "active tenants" shape).
/// </para>
/// </summary>
public static class CrossSourceSpecification
{
    /// <summary>
    /// Resolves the keys of principals matching <paramref name="principalPredicate"/> and returns a
    /// specification selecting the dependents whose foreign key is among them (optionally ANDed with a
    /// local predicate on the dependent's own columns).
    /// </summary>
    /// <typeparam name="TDependent">The dependent entity being filtered (e.g. <c>Session</c>).</typeparam>
    /// <typeparam name="TDependentId">The dependent's identifier type.</typeparam>
    /// <typeparam name="TPrincipal">The cross-source principal entity (e.g. <c>Event</c>).</typeparam>
    /// <typeparam name="TPrincipalId">The principal's identifier type (and the dependent's FK type).</typeparam>
    /// <param name="unitOfWork">Unit of work used to query the principal's own data source.</param>
    /// <param name="principalPredicate">The condition selecting principals (e.g. <c>e =&gt; e.IsPublished</c>).</param>
    /// <param name="dependentForeignKey">The dependent's scalar FK to the principal (e.g. <c>s =&gt; s.EventId</c>).</param>
    /// <param name="localPredicate">Optional predicate on the dependent's own columns, ANDed with the FK filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A specification over the dependent that is translatable on any engine.</returns>
    public static async Task<Specification<TDependent, TDependentId>> BuildAsync<TDependent, TDependentId, TPrincipal, TPrincipalId>(
        IUnitOfWork unitOfWork,
        Expression<Func<TPrincipal, bool>> principalPredicate,
        Expression<Func<TDependent, TPrincipalId>> dependentForeignKey,
        Expression<Func<TDependent, bool>>? localPredicate = null,
        CancellationToken cancellationToken = default)
        where TDependent : IBaseEntity<TDependentId>
        where TDependentId : notnull
        where TPrincipal : AuditableBaseEntity<TPrincipalId>
        where TPrincipalId : notnull
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(principalPredicate);
        ArgumentNullException.ThrowIfNull(dependentForeignKey);

        var principalRepository = unitOfWork.GetReadRepository<TPrincipal, TPrincipalId>();
        var matchingKeys = await principalRepository
            .GetProjectedAsync(p => p.Id, principalPredicate, asTracking: false, cancellationToken)
            .ConfigureAwait(false);

        // Materialize once so the predicate embeds a stable collection EF can translate (IN / ARRAY_CONTAINS).
        var keys = matchingKeys as IReadOnlyList<TPrincipalId> ?? [.. matchingKeys];

        return new InlineSpecification<TDependent, TDependentId>(
            BuildCriteria(dependentForeignKey, keys, localPredicate));
    }

    private static Expression<Func<TDependent, bool>> BuildCriteria<TDependent, TPrincipalId>(
        Expression<Func<TDependent, TPrincipalId>> dependentForeignKey,
        IReadOnlyList<TPrincipalId> keys,
        Expression<Func<TDependent, bool>>? localPredicate)
    {
        var parameter = dependentForeignKey.Parameters[0];

        // keys.Contains(dependent.ForeignKey) → Enumerable.Contains(keys, fk) (translates to IN / ARRAY_CONTAINS).
        Expression body = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(TPrincipalId)],
            Expression.Constant(keys, typeof(IEnumerable<TPrincipalId>)),
            dependentForeignKey.Body);

        if (localPredicate is not null)
        {
            // Rebind the local predicate onto the FK selector's parameter, then AND (no Expression.Invoke,
            // so the combined predicate stays translatable on every provider).
            var reboundLocal = new ParameterReplacer(localPredicate.Parameters[0], parameter)
                .Visit(localPredicate.Body)!;
            body = Expression.AndAlso(reboundLocal, body);
        }

        return Expression.Lambda<Func<TDependent, bool>>(body, parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
