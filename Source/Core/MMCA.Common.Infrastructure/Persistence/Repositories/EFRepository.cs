using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core read-write repository supporting add and update operations.
/// Extends <see cref="EFReadRepository{TEntity,TIdentifierType}"/> with mutation support.
/// Flushing is the unit of work's job, so this type stages changes and never saves.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <remarks>
/// The optional <paramref name="timeProvider"/> and <paramref name="currentUserService"/> serve
/// audit stamping on <see cref="ExecuteUpdateAsync"/>, which bypasses the save pipeline and so
/// stamps the clock and the acting user itself. When absent (direct construction in tests) the
/// system clock is used and the user stamp is skipped.
/// </remarks>
internal sealed class EFRepository<TEntity, TIdentifierType>(
    DbContext context,
    TimeProvider? timeProvider = null,
    ICurrentUserService? currentUserService = null
) : EFReadRepository<TEntity, TIdentifierType>(context), IRepository<TEntity, TIdentifierType>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await Entities.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        await Entities.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// If the entity is already tracked by the context, its values are patched in place
    /// via <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.PropertyValues.SetValues(object)"/>
    /// to avoid an "already tracked" exception. Otherwise, <see cref="DbSet{TEntity}.Update"/>
    /// attaches and marks the entity as modified.
    /// </remarks>
    public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        // FindEntry is an O(1) key lookup against the tracker's identity map (and never
        // falls back to the database), replacing a linear scan of the LocalView.
        var trackedEntry = Entities.Local.FindEntry(entity.Id);
        if (trackedEntry is not null)
            trackedEntry.CurrentValues.SetValues(entity);
        else
            Entities.Update(entity);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void UpdateRange(IEnumerable<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        Entities.UpdateRange(entities);
    }

    /// <inheritdoc />
    public void SetOriginalRowVersion(TEntity entity, byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(rowVersion);

        _context.Entry(entity)
            .Property(nameof(AuditableBaseEntity<>.RowVersion))
            .OriginalValue = rowVersion;
    }

    /// <inheritdoc />
    public void SetOriginalRowVersion(IRowVersioned childEntity, byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(childEntity);
        ArgumentNullException.ThrowIfNull(rowVersion);

        _context.Entry((object)childEntity)
            .Property(nameof(AuditableBaseEntity<>.RowVersion))
            .OriginalValue = rowVersion;
    }

    /// <inheritdoc />
    public async Task<int> ExecuteDeleteAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);
        return await Entities.Where(where).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteUpdateAsync(
        Expression<Func<TEntity, bool>> where,
        Action<IUpdatePropertySetter<TEntity>> setProperties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);
        ArgumentNullException.ThrowIfNull(setProperties);

        var builder = new UpdatePropertySetterBuilder<TEntity>();
        setProperties(builder);
        if (builder.IsEmpty)
            throw new ArgumentException("At least one property assignment is required.", nameof(setProperties));

        // ExecuteUpdate bypasses the save pipeline's audit interceptor, so stamp the
        // modification audit fields here unless the caller assigned them explicitly.
        if (!builder.SetsProperty(nameof(IAuditableEntity.LastModifiedOn)))
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            builder.Set(e => e.LastModifiedOn, (DateTime?)now);
        }

        if (currentUserService?.UserId is { } userId && !builder.SetsProperty(nameof(IAuditableEntity.LastModifiedBy)))
        {
            builder.Set(e => e.LastModifiedBy, userId);
        }

        return await Entities.Where(where).ExecuteUpdateAsync(builder.Apply, cancellationToken).ConfigureAwait(false);
    }
}
