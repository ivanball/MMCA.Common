using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Builds the tenant-scoped form of a cache key or prefix for the caching decorators.
/// </summary>
/// <remarks>
/// <para>
/// The cache is a singleton and cannot see the scoped tenant, so isolation has to be applied where
/// the key is computed. Both decorators use this one helper, which is what keeps reads and
/// invalidations symmetric: a query cached under <c>t:acme:products</c> is evicted by a command
/// whose prefix became <c>t:acme:products</c> through exactly the same transformation.
/// </para>
/// <para>
/// The scoped form is a PREFIX, not a suffix, so prefix eviction keeps working: evicting
/// <c>t:acme:products</c> removes only that tenant's product entries, and nothing a command in one
/// tenant does can evict another tenant's cache.
/// </para>
/// <para>
/// With no tenant resolved the key is returned untouched, so a single-tenant host has exactly the
/// keyspace it had before tenancy shipped and no cache entry is orphaned by upgrading.
/// </para>
/// </remarks>
internal static class TenantCacheKey
{
    /// <summary>The marker that opens a tenant-scoped key; short, because it is on every key.</summary>
    internal const string Marker = "t:";

    /// <summary>
    /// Scopes <paramref name="key"/> to the resolved tenant, or returns it unchanged when no tenant
    /// is resolved.
    /// </summary>
    /// <param name="tenantContext">The scope's tenant context, or null when tenancy is not registered.</param>
    /// <param name="key">The cache key or prefix to scope.</param>
    /// <returns>The effective cache key or prefix.</returns>
    internal static string Scope(ITenantContext? tenantContext, string key) =>
        tenantContext is { IsResolved: true, TenantId: { } tenantId }
            ? string.Concat(Marker, tenantId, ":", key)
            : key;
}
