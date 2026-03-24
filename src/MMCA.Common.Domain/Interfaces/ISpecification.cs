using System.Linq.Expressions;

namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Specification pattern interface that exposes both an expression tree for EF Core
/// LINQ-to-DB translation and a compiled predicate for in-memory evaluation.
/// Used for authorization filtering, query scoping, and domain validation.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification filters.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public interface ISpecification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Gets the expression tree for use in LINQ queries (translated to SQL by EF Core).</summary>
    Expression<Func<TEntity, bool>> Criteria { get; }

    /// <summary>Evaluates the specification against an in-memory entity instance.</summary>
    /// <param name="entity">The entity to test.</param>
    /// <returns><see langword="true"/> if the entity satisfies this specification.</returns>
    bool IsSatisfiedBy(TEntity entity);
}
