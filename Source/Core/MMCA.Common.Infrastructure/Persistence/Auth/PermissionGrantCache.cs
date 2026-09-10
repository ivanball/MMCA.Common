using System.Collections.Frozen;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Permissions;

namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// The shipped <see cref="IPermissionGrantCache"/> and <see cref="IPermissionGrantCacheInvalidator"/>:
/// one <see cref="IMemoryCache"/> entry per role, rebuilt as a whole snapshot from
/// <see cref="IPermissionGrantStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a snapshot rebuild rather than a per-role lazy load.</b> The read is synchronous (the
/// authorization contract is), so a miss cannot go and fetch. Loading every grant in one query on a
/// slow interval, and again immediately after an edit, is what lets the read stay a dictionary lookup
/// with no blocking call anywhere on the request path.
/// </para>
/// <para>
/// <b>Roles that lost their last grant are evicted, not left to expire.</b> The rebuild tracks which
/// role keys it wrote, so a role whose rows were all deleted has its entry removed in the same pass.
/// Without that a revoke would keep granting until the entry's TTL ran out, which is the one staleness
/// direction that is not safe.
/// </para>
/// <para>
/// Singleton, and it opens its own DI scope per load because the store is scoped (it shares the
/// Identity <c>DbContext</c>). That is the <c>RefreshSessionCleanupService</c> shape.
/// </para>
/// </remarks>
/// <param name="cache">The process memory cache the entries live in.</param>
/// <param name="scopeFactory">Factory for the scope each load resolves the store from.</param>
/// <param name="settings">Bound permission-grant settings (the entry lifetime).</param>
internal sealed class PermissionGrantCache(
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<PermissionGrantSettings> settings)
    : IPermissionGrantCache, IPermissionGrantCacheInvalidator, IDisposable
{
    private static readonly FrozenSet<string> Empty = [];

    private readonly PermissionGrantSettings _settings = settings.Value;

    /// <summary>
    /// Serializes rebuilds. Two overlapping rebuilds (the interval refresh and an administration edit
    /// landing together) could otherwise interleave a write from one with the eviction pass of the
    /// other and drop a live role until the next interval.
    /// </summary>
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);

    /// <summary>
    /// The role keys the last rebuild wrote. Replaced wholesale on each rebuild (never mutated in
    /// place) so a reader never observes a half-built set.
    /// </summary>
    private volatile IReadOnlyCollection<string> _cachedRoles = [];

    /// <inheritdoc />
    public IReadOnlySet<string> GetPermissions(string role) =>
        !string.IsNullOrWhiteSpace(role) && cache.TryGetValue(CacheKey(role), out FrozenSet<string>? permissions)
            ? permissions ?? Empty
            : Empty;

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _rebuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IPermissionGrantStore>();

            var grants = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);

            var byRole = grants
                .GroupBy(grant => grant.Role, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(grant => grant.Permission).ToFrozenSet(StringComparer.Ordinal),
                    StringComparer.OrdinalIgnoreCase);

            var lifetime = TimeSpan.FromSeconds(_settings.CacheSeconds);

            foreach (var pair in byRole)
            {
                cache.Set(CacheKey(pair.Key), pair.Value, lifetime);
            }

            // Evict the roles that were cached a moment ago and carry no grant now. Done after the
            // writes so a role that still has grants is never briefly absent.
            foreach (var role in _cachedRoles.Where(role => !byRole.ContainsKey(role)))
            {
                cache.Remove(CacheKey(role));
            }

            _cachedRoles = [.. byRole.Keys];
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    /// <inheritdoc />
    public Task InvalidateAsync(string? role = null, CancellationToken cancellationToken = default)
    {
        // The role argument narrows what a caller MEANS, not what is reloaded: the store read is one
        // query either way, and reloading everything keeps a single code path that cannot leave a
        // second role stale because a caller named only the first.
        _ = role;

        return RefreshAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Present only because the rebuild lock is disposable. The cache entries belong to the host's
    /// <see cref="IMemoryCache"/> and are deliberately left alone: this instance is a singleton, so
    /// disposal happens at shutdown when nothing will read them again.
    /// </remarks>
    public void Dispose() => _rebuildLock.Dispose();

    private static string CacheKey(string role) => $"permgrant:role:{role}";
}
