using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MMCA.Common.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Reads the owned value objects (an <c>OwnsOne</c> address, an <c>OwnsMoney</c> price) of a tracked
/// owner. EF keeps the owner <see cref="EntityState.Unchanged"/> when only an owned dependent
/// changed, because the change is tracked on the owned entry, so the save interceptors walk from the
/// owner to its owned entries to see the edit.
/// </summary>
internal static class OwnedDependents
{
    /// <summary>
    /// Whether any owned reference or owned collection item of <paramref name="entry"/> is being
    /// inserted, updated or deleted in this save.
    /// </summary>
    /// <remarks>
    /// An optional owned reference set to <see langword="null"/> leaves no target entry to read, so
    /// clearing one is not detected here; replacing or editing one is.
    /// </remarks>
    /// <param name="entry">The owner entry.</param>
    /// <returns><see langword="true"/> when an owned dependent carries a pending change.</returns>
    internal static bool HaveChanges(EntityEntry entry) =>
        entry.References.Any(IsChangedOwnedReference)
        || entry.Collections.Any(collection => HasChangedOwnedItem(entry.Context, collection));

    /// <summary>Whether a reference navigation targets an owned type whose entry carries a change.</summary>
    private static bool IsChangedOwnedReference(ReferenceEntry reference) =>
        reference.Metadata.TargetEntityType.IsOwned()
        && reference.TargetEntry is { State: EntityState.Added or EntityState.Modified or EntityState.Deleted };

    /// <summary>Whether an owned collection holds an item whose entry carries a change.</summary>
    private static bool HasChangedOwnedItem(DbContext context, CollectionEntry collection) =>
        collection.Metadata.TargetEntityType.IsOwned()
        && collection.CurrentValue is { } items
        && items.Cast<object?>().Any(item => item is not null && context.Entry(item).State != EntityState.Unchanged);
}
