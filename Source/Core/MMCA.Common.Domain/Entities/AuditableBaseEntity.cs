using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Entities;

/// <summary>
/// Extends <see cref="BaseEntity{TIdentifierType}"/> with soft-delete and audit tracking.
/// Audit fields (<see cref="CreatedOn"/>, <see cref="CreatedBy"/>, etc.) have private setters
/// because they are populated by EF Core's <c>SaveChangesAsync</c> override via
/// <c>entry.Property(...).CurrentValue</c> reflection — the domain layer never sets them directly.
/// </summary>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public abstract class AuditableBaseEntity<TIdentifierType> : BaseEntity<TIdentifierType>, IAuditableEntity
        where TIdentifierType : notnull
{
    /// <summary>
    /// Gets a value indicating whether this entity has been soft-deleted.
    /// Soft-deleted entities remain in the database but are excluded by global query filters.
    /// </summary>
    public virtual bool IsDeleted { get; private set; }

    // Audit properties: private setters are populated by EF Core's SaveChangesAsync override
    // via entry.Property(...).CurrentValue reflection — not by domain code.
#pragma warning disable S1144, CA1819 // Private setters used by EF Core; byte[] required for EF rowversion mapping
    public virtual DateTime CreatedOn { get; private set; }

    public virtual UserIdentifierType CreatedBy { get; private set; }

    public virtual DateTime? LastModifiedOn { get; private set; }

    public virtual UserIdentifierType? LastModifiedBy { get; private set; }

    /// <summary>
    /// Optimistic concurrency token managed by the database. EF Core automatically includes
    /// this value in UPDATE/DELETE WHERE clauses and throws <c>DbUpdateConcurrencyException</c>
    /// if the row was modified by another transaction since it was read.
    /// Configured as <c>[Timestamp]</c> (SQL Server <c>rowversion</c>) in EF entity configurations.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];
#pragma warning restore S1144, CA1819

    /// <summary>
    /// Marks this entity as soft-deleted. Idempotency is enforced — calling
    /// <see cref="Delete"/> on an already-deleted entity returns a failure result.
    /// </summary>
    /// <returns>A success result, or a failure if the entity is already deleted.</returns>
    public virtual Result Delete()
    {
        if (IsDeleted)
        {
            return Result.Failure(
                Error.AlreadyDeleted
                    .WithSource(nameof(Delete))
                    .WithTarget(nameof(AuditableBaseEntity<>)));
        }

        IsDeleted = true;

        return Result.Success();
    }

    /// <summary>
    /// Reverses a soft-delete, restoring the entity to an active state (BR-135).
    /// Only callable from derived classes that explicitly support reactivation.
    /// </summary>
    /// <returns>A success result, or a failure if the entity is not deleted.</returns>
    protected Result Undelete()
    {
        if (!IsDeleted)
        {
            return Result.Failure(
                Error.Invariant(
                    code: "Entity.NotDeleted",
                    message: "Cannot undelete an entity that is not deleted.")
                    .WithSource(nameof(Undelete))
                    .WithTarget(nameof(AuditableBaseEntity<>)));
        }

        IsDeleted = false;

        return Result.Success();
    }
}
