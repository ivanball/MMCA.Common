using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Persistence.Interceptors;

/// <summary>
/// EF Core interceptor that automatically stamps audit fields (<c>CreatedOn/By</c>,
/// <c>LastModifiedOn/By</c>) on all <see cref="IAuditableEntity"/> entries before persistence.
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

        foreach (var entry in context.ChangeTracker.Entries<IAuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.CreatedOn)).CurrentValue = now;
                    entry.Property(nameof(IAuditableEntity.LastModifiedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.LastModifiedOn)).CurrentValue = now;
                    break;
                case EntityState.Modified:
                    entry.Property(nameof(IAuditableEntity.CreatedBy)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.CreatedOn)).IsModified = false;
                    entry.Property(nameof(IAuditableEntity.LastModifiedBy)).CurrentValue = resolvedUserId;
                    entry.Property(nameof(IAuditableEntity.LastModifiedOn)).CurrentValue = now;
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                case EntityState.Deleted:
                default:
                    break;
            }
        }
    }
}
