namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Contract for entities that support soft-delete and audit tracking.
/// All properties are read-only from the domain perspective; the infrastructure layer
/// populates audit fields via EF Core's <c>ChangeTracker</c> during <c>SaveChangesAsync</c>.
/// </summary>
public interface IAuditableEntity
{
    /// <summary>Gets a value indicating whether this entity has been soft-deleted.</summary>
    bool IsDeleted { get; }

    /// <summary>Gets the UTC timestamp when the entity was first persisted.</summary>
    DateTime CreatedOn { get; }

    /// <summary>Gets the identifier of the user who created this entity.</summary>
    UserIdentifierType CreatedBy { get; }

    /// <summary>Gets the UTC timestamp of the most recent modification, or <see langword="null"/> if never modified.</summary>
    DateTime? LastModifiedOn { get; }

    /// <summary>Gets the identifier of the user who last modified this entity, or <see langword="null"/> if never modified.</summary>
    UserIdentifierType? LastModifiedBy { get; }
}
