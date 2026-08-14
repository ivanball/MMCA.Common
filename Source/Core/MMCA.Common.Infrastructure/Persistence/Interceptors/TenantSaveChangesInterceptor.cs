using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Persistence.Interceptors;

/// <summary>
/// EF Core interceptor that owns the write side of multi-tenancy: it stamps
/// <see cref="ITenantEntity.TenantId"/> on inserts and refuses any save that would touch another
/// tenant's row.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate interceptor, one concern each.</b> The audit interceptor stamps identity and
/// timestamps, the domain-event interceptor writes outbox rows, this one enforces the tenant
/// boundary. Registration order in <c>ApplicationDbContext.OnConfiguring</c> is execution order and
/// puts this between them: audit stamps are already written when it runs, and the outbox rows the
/// domain-event interceptor adds afterwards see the final tenant value.
/// </para>
/// <para>
/// <b>Always registered, inert by default.</b> It is a no-op for every entity that does not carry
/// <see cref="ITenantEntity"/>, which is every entity in a host that never adopted tenancy. It also
/// stays out of the way of the system context: when the scope resolved no tenant, an entity that
/// already carries one is written as-is (that is how a background worker drains one tenant's work),
/// and only an untenanted insert is refused, because nothing could supply the missing value.
/// </para>
/// <para>
/// The read side is the named <c>Tenant</c> query filter applied in
/// <c>ApplicationDbContext.ApplyTenantFilters</c>. The two are deliberately independent: a caller
/// who bypasses the filter with EF's own parameterless <c>IgnoreQueryFilters()</c> can read across
/// tenants, but still cannot write across them.
/// </para>
/// </remarks>
public sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is ApplicationDbContext context)
            ApplyTenant(context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is ApplicationDbContext context)
            ApplyTenant(context);

        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// Stamps or verifies the tenant on every tracked <see cref="ITenantEntity"/> being written.
    /// </summary>
    private static void ApplyTenant(ApplicationDbContext context)
    {
        // Read once per save, not once per entry: CurrentTenantId walks a live accessor into the
        // scoped tenant context, and a save must be decided against a single value anyway.
        var currentTenantId = context.CurrentTenantId;

        // Owned types are excluded on both sides of the boundary. The query filter skips them
        // because they are only ever reached through their owner, and the write side skips them for
        // the same reason: an owned value has no independent existence, so the owner's tenant is
        // already the row's tenant and stamping the copy would only invite the two to disagree.
        foreach (var entry in context.ChangeTracker.Entries<ITenantEntity>()
            .Where(entry => !entry.Metadata.IsOwned()))
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampOrVerifyInsert(entry, currentTenantId);
                    break;
                case EntityState.Modified:
                    VerifyExistingRow(entry, currentTenantId, "update");
                    break;
                case EntityState.Deleted:
                    VerifyExistingRow(entry, currentTenantId, "delete");
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Assigns the scope's tenant to a new row, or rejects the insert when the row already names a
    /// different one. An untenanted insert from an untenanted scope is rejected: silently writing a
    /// row no tenant can ever read is worse than failing the save.
    /// </summary>
    private static void StampOrVerifyInsert(EntityEntry<ITenantEntity> entry, string? currentTenantId)
    {
        var property = entry.Property(nameof(ITenantEntity.TenantId));
        var declared = property.CurrentValue as string;
        var entityType = EntityTypeName(entry);

        if (currentTenantId is null)
        {
            if (string.IsNullOrEmpty(declared))
                throw CrossTenantWriteException.ForUnresolvedTenant(entityType);

            // A system scope inserting an explicitly tenanted row (a seeder, a per-tenant job).
            return;
        }

        if (string.IsNullOrEmpty(declared))
        {
            property.CurrentValue = currentTenantId;
            return;
        }

        if (!string.Equals(declared, currentTenantId, StringComparison.Ordinal))
            throw CrossTenantWriteException.ForMismatch(entityType, "insert", currentTenantId, declared);
    }

    /// <summary>
    /// Rejects an update or delete of a row belonging to another tenant, and an update that would
    /// move a row between tenants. Inert when the scope resolved no tenant: the system context is
    /// deliberately unrestricted, exactly as it is on the read side.
    /// </summary>
    private static void VerifyExistingRow(
        EntityEntry<ITenantEntity> entry,
        string? currentTenantId,
        string operation)
    {
        if (currentTenantId is null)
            return;

        var property = entry.Property(nameof(ITenantEntity.TenantId));

        // Both sides are checked: the original value catches touching another tenant's row, the
        // current value catches reassigning this row to another tenant in the same save.
        var offending = FirstForeignTenant(
            property.OriginalValue as string,
            property.CurrentValue as string,
            currentTenantId);

        if (offending is not null)
        {
            throw CrossTenantWriteException.ForMismatch(
                EntityTypeName(entry), operation, currentTenantId, offending);
        }
    }

    /// <summary>
    /// The first of the two recorded tenant values that belongs to someone other than
    /// <paramref name="currentTenantId"/>, or <see langword="null"/> when both are this tenant's (or
    /// absent). The original is reported in preference to the current one, because "you touched
    /// another tenant's row" is the more useful diagnosis than "you renamed its owner".
    /// </summary>
    private static string? FirstForeignTenant(string? original, string? current, string currentTenantId)
    {
        if (IsForeign(original, currentTenantId))
            return original;

        return IsForeign(current, currentTenantId) ? current : null;
    }

    /// <summary>Whether a recorded tenant value is present and names a different tenant.</summary>
    private static bool IsForeign(string? value, string currentTenantId) =>
        !string.IsNullOrEmpty(value) && !string.Equals(value, currentTenantId, StringComparison.Ordinal);

    /// <summary>The entity's CLR type name, used in the failure message.</summary>
    private static string EntityTypeName(EntityEntry entry) =>
        entry.Metadata.ClrType.FullName ?? entry.Metadata.ClrType.Name;
}
