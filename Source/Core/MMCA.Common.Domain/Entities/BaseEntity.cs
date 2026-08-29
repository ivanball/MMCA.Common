using System.Diagnostics.CodeAnalysis;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Entities;

/// <summary>
/// Base class for all domain entities. Uses <c>required init</c> for <see cref="Id"/>
/// so the identifier is immutable after construction: factory methods set the value once
/// and EF Core materializes it via the parameterless constructor.
/// </summary>
/// <typeparam name="TIdentifierType">
/// The identifier type, aliased per-entity via global usings
/// (e.g., <c>OrderIdentifierType = int</c>).
/// </typeparam>
/// <remarks>
/// Equality is IDENTITY equality: two instances are equal when they are the same concrete type and
/// carry the same ASSIGNED <see cref="Id"/>, so an aggregate loaded twice through different contexts
/// compares equal instead of answering the reference comparison the CLR would otherwise give.
/// <para>
/// A TRANSIENT instance (one whose <see cref="Id"/> still holds the identifier type's default,
/// which is the state of an <c>[IdValueGenerated]</c> entity before the database stamps its key) is
/// never equal to another transient instance unless the two are the same reference: a default id
/// means "not identified yet", not "identified as zero".
/// </para>
/// <para>
/// Intentionally does not implement <see cref="IEquatable{T}"/> (S4035: an unsealed
/// <c>IEquatable&lt;T&gt;</c> breaks the equality contract for subclasses). Equality is provided via
/// the <see cref="object.Equals(object?)"/> override below, type-guarded so two entities are equal
/// only when they are the same concrete type; a sealed derived entity may add a strongly-typed
/// <c>IEquatable&lt;TSelf&gt;</c> on top. The same trade-off, for the same reason, is documented on
/// <c>MMCA.Common.Shared.ValueObjects.Enumeration</c> and <c>MMCA.Common.Shared.Auth.RoleValue</c>.
/// </para>
/// </remarks>
public abstract class BaseEntity<TIdentifierType> : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    public required TIdentifierType Id { get; init; }

    /// <summary>
    /// Compares two entities by identity, treating <see langword="null"/> as equal only to
    /// <see langword="null"/>. Delegates to <see cref="Equals(object?)"/>, so the concrete-type and
    /// assigned-id guards apply exactly as they do there.
    /// </summary>
    /// <param name="left">The left-hand entity, or <see langword="null"/>.</param>
    /// <param name="right">The right-hand entity, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both operands identify the same entity.</returns>
    [SuppressMessage(
        "Major Code Smell",
        "S3875:\"operator==\" should not be overloaded on reference types",
        Justification = "Identity equality is the point of an entity base: the same row read twice is the same entity, and callers write `a == b` for that. The rule's own escape hatch is IEquatable<T>, which this UNSEALED base deliberately does not implement because that breaks the equality contract for subclasses (S4035, and the same trade-off documented on Enumeration/RoleValue). The operator therefore delegates to the type-guarded Equals override rather than adding a second, weaker equality path.")]
    public static bool operator ==(BaseEntity<TIdentifierType>? left, BaseEntity<TIdentifierType>? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>
    /// The negation of the <c>==</c> operator above.
    /// </summary>
    /// <param name="left">The left-hand entity, or <see langword="null"/>.</param>
    /// <param name="right">The right-hand entity, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the operands do not identify the same entity.</returns>
    public static bool operator !=(BaseEntity<TIdentifierType>? left, BaseEntity<TIdentifierType>? right)
        => !(left == right);

    /// <summary>
    /// Identity equality: <see langword="true"/> for the same reference, or for another instance of
    /// the SAME concrete type whose <see cref="Id"/> is assigned and equal to this one's. A derived
    /// type never compares equal to its base or to a sibling, and two transient instances (both ids
    /// still at the type default) are equal only when they are the same reference.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> identifies the same entity.</returns>
    public override bool Equals(object? obj)
        => ReferenceEquals(this, obj)
            || obj is BaseEntity<TIdentifierType> other
            && other.GetType() == GetType()
            && HasAssignedId(Id)
            && HasAssignedId(other.Id)
            && EqualityComparer<TIdentifierType>.Default.Equals(Id, other.Id);

    /// <summary>
    /// Hashes the concrete type together with <see cref="Id"/>, matching
    /// <see cref="Equals(object?)"/> for every entity that already has an id.
    /// </summary>
    /// <remarks>
    /// CAVEAT for database-generated keys: the hash CHANGES when the id is stamped, so an entity
    /// whose key is assigned by the store (<c>[IdValueGenerated]</c>) must not be put in a
    /// hash-based collection (<see cref="HashSet{T}"/>, a dictionary key) before the save that
    /// assigns it. Bucket it while transient and it becomes unfindable in its own collection after
    /// the save. Code that has to track pre-save instances keys them by reference instead: see the
    /// <c>ReferenceEqualityComparer</c> sets in <c>DomainEventSaveChangesInterceptor</c> and
    /// <c>AuditableAggregateRootEntity.RemoveDomainEvents</c>.
    /// </remarks>
    /// <returns>A hash code combining the concrete type and the identifier.</returns>
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>
    /// Reports whether <paramref name="id"/> holds a real identifier rather than the identifier
    /// type's default. The comparer handles both shapes the alias can take: zero for an integer
    /// key, <see langword="null"/> for a reference key.
    /// </summary>
    /// <param name="id">The identifier to test.</param>
    /// <returns><see langword="true"/> when the identifier has been assigned.</returns>
    private static bool HasAssignedId(TIdentifierType id)
        => !EqualityComparer<TIdentifierType>.Default.Equals(id, default);
}
