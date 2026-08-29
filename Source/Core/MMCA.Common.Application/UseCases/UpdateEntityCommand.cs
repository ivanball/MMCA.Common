namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Generic update command for any aggregate root entity: the entity's primary key, the caller's
/// last-observed concurrency token, and the update request the aggregate has to accept. The
/// <typeparamref name="TEntity"/> type parameter distinguishes update handlers for different entity
/// types that share the same identifier type, and supplies the default cache prefix evicted after a
/// successful update.
/// </summary>
/// <remarks>
/// <para>
/// The command carries the request rather than flattening it, so it implements
/// <see cref="ICommandWithRequest{TRequest}"/> and the framework's validator bridge registers a
/// <c>CommandRequestValidator</c> for it automatically: a module only writes
/// <c>IValidator&lt;TUpdateRequest&gt;</c> and the command is validated before the transaction opens.
/// </para>
/// <para>
/// The mutation itself is not here. It lives on the aggregate, reached through the module's
/// <c>IEntityUpdateApplier</c>, which is what keeps one generic command usable for every entity
/// without the command knowing a single field name.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type to update.</typeparam>
/// <typeparam name="TUpdateRequest">The update request the applier consumes.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="Id">The primary key of the entity to update.</param>
/// <param name="Request">The update request payload.</param>
/// <param name="RowVersion">
/// The caller's last-observed concurrency token (ADR-035), or <see langword="null"/> to skip the
/// stale-view check. A body-less or legacy caller simply passes nothing.
/// </param>
public sealed record UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>(
    TIdentifierType Id,
    TUpdateRequest Request,
    byte[]? RowVersion = null)
    : ICommandWithRequest<TUpdateRequest>, ICacheInvalidating
    where TIdentifierType : notnull
{
    /// <summary>
    /// Gets the cache key prefix evicted after a successful update. Defaults to the
    /// aggregate-prefix convention every consumer already keys its cached reads under
    /// (<c>{entity full name}:</c>), because the generic controller constructs this command
    /// itself and cannot supply one. Set it to an empty string to opt out of invalidation.
    /// </summary>
    public string CachePrefix { get; init; } = typeof(TEntity).FullName + ":";
}
