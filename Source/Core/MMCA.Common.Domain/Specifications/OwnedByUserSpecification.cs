using System.Linq.Expressions;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Specifications;

/// <summary>
/// Specification that restricts a query to the rows created by a single user, using the
/// audit field <see cref="AuditableBaseEntity{TIdentifierType}.CreatedBy"/> as the ownership
/// marker. Use it for "my own records" reads (an attendee sees only the answers they submitted);
/// callers with a bypass role simply do not apply it.
/// </summary>
/// <remarks>
/// The constraint is the concrete <see cref="AuditableBaseEntity{TIdentifierType}"/> base class
/// rather than <c>IAuditableEntity</c> on purpose: the criteria must stay EF-translatable, and a
/// member access declared on an interface is not guaranteed to map to the entity's audit column.
/// </remarks>
/// <typeparam name="TEntity">The auditable entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <param name="userId">The owning user's identifier.</param>
public sealed class OwnedByUserSpecification<TEntity, TIdentifierType>(UserIdentifierType userId)
    : Specification<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>Gets the owning user's identifier this specification filters on.</summary>
    public UserIdentifierType UserId { get; } = userId;

    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria =>
        e => e.CreatedBy == UserId;
}
