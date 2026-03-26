using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core read-write repository supporting add, update, and save operations.
/// Extends <see cref="EFReadRepository{TEntity,TIdentifierType}"/> with mutation support.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal sealed class EFRepository<TEntity, TIdentifierType>(
    DbContext context
) : EFReadRepository<TEntity, TIdentifierType>(context), IRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await Entities.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// If the entity is already tracked by the context, its values are patched in place
    /// via <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues.SetValues(object)"/>
    /// to avoid an "already tracked" exception. Otherwise, <see cref="DbSet{TEntity}.Update"/>
    /// attaches and marks the entity as modified.
    /// On failure, all pending changes are rolled back to <see cref="EntityState.Unchanged"/>
    /// to keep the context in a usable state.
    /// </remarks>
    public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        try
        {
            var trackedEntity = Entities.Local.FirstOrDefault(e => e.Id.Equals(entity.Id));
            if (trackedEntity is not null)
                _context.Entry(trackedEntity).CurrentValues.SetValues(entity);
            else
                Entities.Update(entity);
            return Task.CompletedTask;
        }
        catch (DbUpdateException ex)
        {
            throw new DbUpdateException(GetFullErrorTextAndRollbackEntityChanges(ex), ex);
        }
    }

    /// <inheritdoc />
    public int Save() => _context.SaveChanges();

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Rolls back all Added/Modified entries to Unchanged and persists the reset
    /// so the context is left in a clean state after a failed update.
    /// </summary>
    /// <returns>The full error text from the original or rollback exception.</returns>
    private string GetFullErrorTextAndRollbackEntityChanges(DbUpdateException exception)
    {
        var entries = _context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();

        foreach (var entry in entries)
        {
            try
            {
                entry.State = EntityState.Unchanged;
            }
            catch (InvalidOperationException)
            {
                // Entry may be in a state that cannot transition to Unchanged (e.g. keyless).
            }
        }

        try
        {
            _context.SaveChanges();
            return exception.ToString();
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
    }
}
