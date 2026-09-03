namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Configuration for the framework's caching defaults, bound from the <c>Cache</c> section.
/// All properties have sensible defaults so the section is optional in <c>appsettings.json</c>,
/// and the defaults reproduce the hard-coded policy in <see cref="CacheOptions"/> exactly: a host
/// that configures nothing behaves as it did before this section existed.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is fail-open. The cache is an optimization, never the system of record, so no
/// value in this section can turn a cache outage or a slow populate into an error: a miss, an
/// unreachable cache, or an expired <see cref="PopulateLockTimeout"/> all degrade the request to an
/// uncached read that still runs the real handler and still answers correctly.
/// </para>
/// <para>
/// The section is shared with <c>Cache:KeyPrefix</c> (bound to <c>CacheKeyPrefixOptions</c>) and
/// with the Application layer's <c>QueryCachePipelineSettings</c>, which reads the same
/// <c>Cache:PopulateLockTimeout</c> key from a layer that cannot reference this assembly.
/// </para>
/// </remarks>
public sealed class CacheSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Cache";

    /// <summary>
    /// Gets the absolute time-to-live applied to a cache entry whose caller supplies no expiration.
    /// Defaults to <see cref="CacheOptions.DefaultDuration"/> (30 seconds), which stays the single
    /// source of truth for the value so the configured and hard-coded paths cannot drift apart.
    /// </summary>
    public TimeSpan DefaultDuration { get; init; } = CacheOptions.DefaultDuration;

    /// <summary>
    /// Gets the ceiling applied to the in-process (L1) copy of a two-level cache entry, so a replica
    /// that never sees an invalidation still re-reads L2 within this window. The effective L1
    /// lifetime is the shorter of this value and the entry's own TTL.
    /// <see langword="null"/> (the default) keeps the built-in 30-second ceiling
    /// (<c>HybridCacheService.LocalCacheDefault</c>). Ignored by the single-level cache services,
    /// which have no L1 of their own.
    /// </summary>
    public TimeSpan? LocalCacheDuration { get; init; }

    /// <summary>
    /// Gets how long a request that missed the cache waits for the per-key populate lock before
    /// giving up and running the query uncached. Defaults to
    /// <see cref="Timeout.InfiniteTimeSpan"/>: waiters block until the one request that took the
    /// lock has populated the entry, which is the stampede protection this lock exists for.
    /// </summary>
    /// <remarks>
    /// A finite value trades that protection for a latency bound: once it elapses, the waiter
    /// proceeds UNCACHED and executes the inner handler itself, so a populate that is pathologically
    /// slow (or a handler wedged under a shared stripe) cannot hold a queue of requests behind it.
    /// The cost is that several requests can then run the same query at once. Zero or a negative
    /// value means no bound, exactly like the default.
    /// </remarks>
    public TimeSpan PopulateLockTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}
