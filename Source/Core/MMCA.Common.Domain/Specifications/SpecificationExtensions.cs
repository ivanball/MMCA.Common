using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Specifications;

/// <summary>
/// Fluent composition members for <see cref="ISpecification{TEntity, TIdentifierType}"/>.
/// <para>
/// They are thin factories over <see cref="AndSpecification{TEntity, TIdentifierType}"/>,
/// <see cref="OrSpecification{TEntity, TIdentifierType}"/> and
/// <see cref="NotSpecification{TEntity, TIdentifierType}"/>, so a composed predicate reads left to
/// right instead of inside out:
/// </para>
/// <code>
/// // Nested constructors:
/// var spec = new AndSpecification&lt;Order, int&gt;(
///     new OwnedByUserSpecification&lt;Order, int&gt;(userId),
///     new NotSpecification&lt;Order, int&gt;(new CancelledOrderSpecification()));
///
/// // The same thing, fluently:
/// var spec = new OwnedByUserSpecification&lt;Order, int&gt;(userId)
///     .And(new CancelledOrderSpecification().Not());
/// </code>
/// <para>
/// Every composer merges the underlying criteria by parameter substitution, so the composed
/// <c>Criteria</c> stays translatable on every provider, and caches the composed expression per
/// instance. Hold on to the composed specification (a field, a local) rather than rebuilding it per
/// request if the composition itself is on a hot path.
/// </para>
/// </summary>
public static class SpecificationExtensions
{
    extension<TEntity, TIdentifierType>(ISpecification<TEntity, TIdentifierType> specification)
        where TEntity : IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        /// <summary>
        /// Returns a specification satisfied only when both this specification and
        /// <paramref name="other"/> are satisfied.
        /// </summary>
        /// <param name="other">The specification to AND with this one.</param>
        /// <returns>The composed AND specification.</returns>
        /// <example>
        /// <code>
        /// var activeAndMine = new ActiveSpecification()
        ///     .And(new OwnedByUserSpecification&lt;Ticket, int&gt;(currentUserId));
        /// </code>
        /// </example>
        public AndSpecification<TEntity, TIdentifierType> And(ISpecification<TEntity, TIdentifierType> other)
        {
            ArgumentNullException.ThrowIfNull(specification);
            ArgumentNullException.ThrowIfNull(other);

            return new AndSpecification<TEntity, TIdentifierType>(specification, other);
        }

        /// <summary>
        /// Returns a specification satisfied when either this specification or
        /// <paramref name="other"/> is satisfied.
        /// </summary>
        /// <param name="other">The specification to OR with this one.</param>
        /// <returns>The composed OR specification.</returns>
        /// <example>
        /// <code>
        /// var visible = new PublishedSpecification()
        ///     .Or(new OwnedByUserSpecification&lt;Article, int&gt;(currentUserId));
        /// </code>
        /// </example>
        public OrSpecification<TEntity, TIdentifierType> Or(ISpecification<TEntity, TIdentifierType> other)
        {
            ArgumentNullException.ThrowIfNull(specification);
            ArgumentNullException.ThrowIfNull(other);

            return new OrSpecification<TEntity, TIdentifierType>(specification, other);
        }

        /// <summary>
        /// Returns a specification satisfied exactly when this one is not.
        /// </summary>
        /// <returns>The composed NOT specification.</returns>
        /// <example>
        /// <code>
        /// var notArchived = new ArchivedSpecification().Not();
        /// </code>
        /// </example>
        public NotSpecification<TEntity, TIdentifierType> Not()
        {
            ArgumentNullException.ThrowIfNull(specification);

            return new NotSpecification<TEntity, TIdentifierType>(specification);
        }
    }
}
