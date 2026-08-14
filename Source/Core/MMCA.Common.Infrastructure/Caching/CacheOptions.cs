using Microsoft.Extensions.Caching.Distributed;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Factory for <see cref="DistributedCacheEntryOptions"/> with a sensible default expiration.
/// Centralises cache TTL policy so callers don't need to construct options manually.
/// </summary>
public static class CacheOptions
{
    /// <summary>
    /// Gets the default time-to-live applied when a caller passes no expiration. Exposed as a bare
    /// <see cref="TimeSpan"/> so implementations that do not speak
    /// <see cref="DistributedCacheEntryOptions"/> (<c>HybridCacheService</c>, whose entry options are
    /// its own type) still default to the same policy rather than a second hard-coded 30 seconds.
    /// </summary>
    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the default cache entry options with a 30-second absolute expiration.
    /// </summary>
    public static DistributedCacheEntryOptions DefaultExpiration => new()
    {
        AbsoluteExpirationRelativeToNow = DefaultDuration
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
