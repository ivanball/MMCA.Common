using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core read-write repository: staged adds, original row-version handling for optimistic
/// concurrency, and set-based <see cref="ExecuteDeleteAsync"/> / <see cref="ExecuteUpdateAsync"/>.
/// Extends <see cref="EFReadRepository{TEntity,TIdentifierType}"/>; tracked entities are updated
/// by mutating them, so there is no per-entity update method.
/// Flushing is the unit of work's job, so this type stages changes and never saves.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <remarks>
/// <paramref name="timeProvider"/> and the optional <paramref name="currentUserService"/> serve
/// audit stamping on <see cref="ExecuteUpdateAsync"/>, which bypasses the save pipeline and so
/// stamps the clock and the acting user itself. Without a user service (direct construction in
/// tests) the user stamp is skipped.
/// </remarks>
internal sealed class EFRepository<TEntity, TIdentifierType>(
    DbContext context,
    TimeProvider timeProvider,
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
    public Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return Entities.AddRangeAsync(entities, cancellationToken);
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
    public void TouchConcurrencyToken(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var entry = _context.Entry(entity);

        // Only an UNCHANGED root needs the nudge. A root the applier already modified emits its own
        // UPDATE, which already carries the concurrency predicate, and marking a property modified on
        // a Deleted or Detached entry would be wrong rather than merely redundant.
        if (entry.State != EntityState.Unchanged)
        {
            return;
        }

        // Marking the audit stamp modified is what puts the row in the UPDATE statement. The audit
        // interceptor then fills in the value, so the write is a real edit record rather than a
        // no-op touch, and EF appends WHERE RowVersion = @original from the token the caller sent.
        entry.Property(nameof(AuditableBaseEntity<>.LastModifiedOn)).IsModified = true;
    }

    /// <inheritdoc />
    public Task<int> ExecuteDeleteAsync(
        Expression<Func<TEntity, bool>> where,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(where);
        return Entities.Where(where).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ExecuteUpdateAsync(
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
            var now = timeProvider.GetUtcNow().UtcDateTime;
            builder.Set(e => e.LastModifiedOn, (DateTime?)now);
        }

        // With no current user (a background or system scope) the editor is the default sentinel,
        // exactly as the save pipeline attributes a system save; leaving the column alone would keep
        // crediting the change to the previous human editor.
        if (!builder.SetsProperty(nameof(IAuditableEntity.LastModifiedBy)))
        {
            builder.Set(e => e.LastModifiedBy, currentUserService?.UserId ?? default);
        }

        StampRowVersion(builder);

        return Entities.Where(where).ExecuteUpdateAsync(builder.Apply, cancellationToken);
    }

    /// <summary>
    /// Re-stamps <c>RowVersion</c> for a set-based update on an engine with no server-generated row
    /// version (PostgreSQL, SQLite). There the token is written by the audit interceptor, which
    /// ExecuteUpdate also bypasses, so without a fresh value a client still holding the pre-update
    /// token would pass its If-Match check. SQL Server's <c>rowversion</c> moves by itself.
    /// </summary>
    private void StampRowVersion(UpdatePropertySetterBuilder<TEntity> builder)
    {
        if (EngineCapabilities?.RowVersion == RowVersionStrategy.ClientStamped
            && !builder.SetsProperty(nameof(IRowVersioned.RowVersion))
            && _context.Model.FindEntityType(typeof(TEntity))?.FindProperty(nameof(IRowVersioned.RowVersion)) is not null)
        {
            builder.Set(e => e.RowVersion, Guid.NewGuid().ToByteArray());
        }
    }
}
