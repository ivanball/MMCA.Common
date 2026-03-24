using Microsoft.Extensions.Caching.Distributed;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Factory for <see cref="DistributedCacheEntryOptions"/> with a sensible default expiration.
/// Centralises cache TTL policy so callers don't need to construct options manually.
/// </summary>
public static class CacheOptions
{
    /// <summary>
    /// Gets the default cache entry options with a 30-second absolute expiration.
    /// </summary>
    public static DistributedCacheEntryOptions DefaultExpiration => new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Creates cache entry options with the specified expiration, falling back to <see cref="DefaultExpiration"/> when <paramref name="expiration"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="expiration">Optional custom expiration duration.</param>
    /// <returns>A configured <see cref="DistributedCacheEntryOptions"/> instance.</returns>
    public static DistributedCacheEntryOptions Create(TimeSpan? expiration) =>
        expiration is not null ?
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiration } :
            DefaultExpiration;
}
