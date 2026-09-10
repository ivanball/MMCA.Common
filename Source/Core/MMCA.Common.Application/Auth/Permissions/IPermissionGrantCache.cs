namespace MMCA.Common.Application.Auth.Permissions;

/// <summary>
/// The synchronous read model over the stored permission grants: what
/// <see cref="LayeredPermissionRegistry"/> consults once the compiled registry has said no.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cache is part of the contract.</b> <c>IPermissionRegistry.HasPermission</c> is
/// synchronous and sits on the hot path of every permission-gated request. Reading a row per check
/// would need blocking I/O there, so the grants are held in memory and this interface is the boundary
/// where that becomes visible instead of hidden behind a sync-over-async call.
/// </para>
/// <para>
/// <b>Fail closed while cold.</b> Before the first successful <see cref="RefreshAsync"/> the cache is
/// empty, and an empty cache grants nothing. A stored grant is therefore invisible for the moments
/// between process start and the first load, which is why the shipped implementation is primed by a
/// hosted service at startup. The compiled registry is unaffected: it answers from the first request.
/// </para>
/// </remarks>
public interface IPermissionGrantCache
{
    /// <summary>
    /// The stored permissions currently cached for a role, empty when the role has none (or when the
    /// cache has not been loaded yet).
    /// </summary>
    /// <param name="role">The role name, matched case-insensitively.</param>
    /// <returns>The role's stored permissions.</returns>
    IReadOnlySet<string> GetPermissions(string role);

    /// <summary>
    /// Reloads the whole snapshot from the store. Called at startup, on the configured interval, and
    /// explicitly by <see cref="IPermissionGrantCacheInvalidator"/> after a grant changes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the snapshot has been replaced.</returns>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Drops the cached grants for a role (or for every role) and reloads them, so an administration edit
/// takes effect on the next request rather than at the end of the cache interval.
/// </summary>
/// <remarks>
/// Invalidation is per process. With several replicas over one database, the replica that made the
/// edit is immediately correct and the others catch up on their own refresh interval, which is what
/// <c>PermissionGrantSettings.CacheSeconds</c> bounds. That is deliberate: a cross-replica push would
/// need a broker on the authorization path, and the window it would close is a grant taking effect a
/// few seconds late, never a revoked grant staying live past the interval.
/// </remarks>
public interface IPermissionGrantCacheInvalidator
{
    /// <summary>
    /// Invalidates the cached grants and reloads them.
    /// </summary>
    /// <param name="role">The role to invalidate, or <see langword="null"/> for every role.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the cache reflects the store again.</returns>
    Task InvalidateAsync(string? role = null, CancellationToken cancellationToken = default);
}
