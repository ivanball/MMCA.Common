using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Persistence.Interceptors;

/// <summary>
/// EF Core interceptor that automatically stamps audit fields (<c>CreatedOn/By</c>,
/// <c>LastModifiedOn/By</c>, <c>DeletedOn/By</c>) on all <see cref="IAuditableEntity"/> entries
/// before persistence.
/// <para>
/// The soft-delete stamps are driven by the <see cref="IAuditableEntity.IsDeleted"/> TRANSITION,
/// not by its value: they are written when the flag goes false to true (<c>Delete()</c>) and
/// cleared when it goes true to false (<c>Undelete()</c>). A save that touches an
/// already-deleted row therefore leaves the original delete stamp intact, exactly as
/// <c>CreatedOn/By</c> survive every later update.
/// </para>
/// <para>
/// On PostgreSQL and SQLite it also manages the <see cref="IRowVersioned.RowVersion"/> concurrency
/// token: neither engine has a server-generated row version, so every insert and update writes a
/// fresh random 16-byte value. EF keeps the value the row was loaded with as the original, so the
/// UPDATE still carries it in its WHERE clause and a concurrent writer is detected. SQL Server's
/// <c>rowversion</c> column is database-generated and is never touched here.
/// </para>
/// <para>
/// An owner whose only change is inside an owned value object (an <c>OwnsOne</c> address, an
/// <c>OwnsMoney</c> amount) is stamped as modified too: EF tracks that edit on the owned entry and
/// leaves the owner unchanged, so without the owner-side walk the row would be rewritten with stale
/// <c>LastModifiedOn/By</c> and, on PostgreSQL and SQLite, an unchanged <c>RowVersion</c>. Clearing
/// an optional owned reference to <see langword="null"/> leaves no owned entry to see and is not
/// stamped.
/// </para>
/// </summary>
/// <param name="timeProvider">Provides UTC timestamps for audit fields.</param>
public sealed class AuditSaveChangesInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext context)
            StampAuditFields(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is ApplicationDbContext context)
            StampAuditFields(context);

        return base.SavingChanges(eventData, result);
    }

    private void StampAuditFields(ApplicationDbContext context)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var resolvedUserId = context.CurrentSaveUserId ?? default;
        var stampsRowVersion = context.Engine.Capabilities.RowVersion == RowVersionStrategy.ClientStamped;

        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            // An edit confined to an owned value object leaves the owner Unchanged (the owned entry
            // carries the state), yet it is an update of the owner's row: stamp it as one.
            var state = entry.State == EntityState.Unchanged && OwnedDependents.HaveChanges(entry)
                ? EntityState.Modified
                : entry.State;

            switch (state)
            {
                case EntityState.Added:
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.CreatedOn)).CurrentValue = now;
                    entry.Property(nameof(IAuditableEntity.LastModifiedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.LastModifiedOn)).CurrentValue = now;

                    // A brand new row has no prior state, so "was deleted" is false by construction:
                    // an entity inserted already soft-deleted still gets its delete stamp.
                    StampSoftDeleteTransition(entry, now, resolvedUserId, wasDeleted: false);
                    StampRowVersion(entry, stampsRowVersion);
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.CreatedOn)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.LastModifiedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.LastModifiedOn)).CurrentValue = now;

                    StampSoftDeleteTransition(entry, now, resolvedUserId, WasDeleted(entry));
                    StampRowVersion(entry, stampsRowVersion);
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                case EntityState.Deleted:
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Writes a fresh application-managed concurrency token on an engine with no server-generated
    /// row version. Setting the current value marks the property modified while EF keeps the loaded
    /// value as the original for the UPDATE's WHERE clause.
    /// </summary>
    private static void StampRowVersion(EntityEntry<IAuditableEntity> entry, bool stampsRowVersion)
    {
        if (stampsRowVersion && entry.Metadata.FindProperty(nameof(IRowVersioned.RowVersion)) is not null)
        {
            entry.Property(nameof(IRowVersioned.RowVersion)).CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }

    /// <summary>The soft-delete flag as it stands in the database, before this save's changes.</summary>
    private static bool WasDeleted(EntityEntry<IAuditableEntity> entry) =>
        entry.Property(nameof(IAuditableEntity.IsDeleted)).OriginalValue is true;

    /// <summary>
    /// Writes or clears <c>DeletedOn/By</c> when, and only when, the soft-delete flag actually
    /// changed during this save. No transition means no write, so an update to an already-deleted
    /// row keeps the stamps of the delete that produced it.
    /// </summary>
    private static void StampSoftDeleteTransition(
        EntityEntry<IAuditableEntity> entry,
        DateTime now,
        UserIdentifierType resolvedUserId,
        bool wasDeleted)
    {
        var isDeleted = entry.Property(nameof(IAuditableEntity.IsDeleted)).CurrentValue is true;
        if (isDeleted == wasDeleted)
        {
            return;
        }

        entry.Property(nameof(IAuditableEntity.DeletedBy)).CurrentValue = isDeleted ? resolvedUserId : (UserIdentifierType?)null;
        entry.Property(nameof(IAuditableEntity.DeletedOn)).CurrentValue = isDeleted ? now : (DateTime?)null;
    }
}
