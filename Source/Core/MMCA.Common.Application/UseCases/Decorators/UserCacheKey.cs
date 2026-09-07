using MMCA.Common.Application.Users;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Builds the caller-scoped form of a cache key for the caching decorators, exactly as
/// <see cref="TenantCacheKey"/> builds the tenant-scoped form.
/// </summary>
/// <remarks>
/// <para>
/// SEC-Common-37. A per-user query ("my orders", "my profile") whose author followed
/// <c>IQueryCacheable</c>'s guidance to include "all parameters that affect the query result" still
/// leaves out the one parameter that is not on the request: WHO is asking. Two users then compute
/// the same key against one shared cache and the second is served the first's personal data for the
/// whole cache duration.
/// </para>
/// <para>
/// The caller marker is a SUFFIX, unlike the tenant one, and that difference is deliberate.
/// <c>ICacheInvalidating</c> commands evict by PREFIX, so a caller segment inserted ahead of the
/// key (<c>t:acme:u:7:Sales:MyOrders</c>) would put every user's entry outside the prefix the
/// invalidating command computes and nothing would ever be evicted. Appending it
/// (<c>t:acme:Sales:MyOrders:u:7</c>) leaves the existing prefix intact, so one command still clears
/// every caller's copy. Over-eviction across users is harmless; the leak this closes is not.
/// </para>
/// </remarks>
internal static class UserCacheKey
{
    /// <summary>The marker that opens the caller segment; short, because it is on every key.</summary>
    internal const string Marker = ":u:";

    /// <summary>
    /// Scopes <paramref name="key"/> to the caller when the query declares itself caller-scoped,
    /// or returns it unchanged otherwise.
    /// </summary>
    /// <param name="query">The query being cached.</param>
    /// <param name="key">The cache key to scope.</param>
    /// <returns>The effective cache key.</returns>
    internal static string Scope(object? query, string key) =>
        query is IUserScopedRequest userScoped and not UseCases.Markers.ISharedQueryCache
            ? string.Concat(key, Marker, userScoped.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : key;
}
