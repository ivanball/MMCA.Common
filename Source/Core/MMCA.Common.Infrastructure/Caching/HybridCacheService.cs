using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Two-level cache backed by <see cref="HybridCache"/>: an in-process L1 in front of the registered
/// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> L2, with serialization,
/// L1 promotion and stampede protection supplied by the platform.
/// <para>
/// Registered only by <c>AddCommonHybridCache</c>. A host that does not call it keeps
/// <see cref="DistributedCacheService"/> or <see cref="MemoryCacheService"/> untouched.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Every key is written under a disjoint <c>hc:</c> keyspace</b> (<c>{prefix}hc:{key}</c>). This is
/// the structural form of the lesson recorded on <see cref="DistributedCacheService.IncrementAsync"/>:
/// two serialization formats must never share one key. <see cref="HybridCache"/> writes its own
/// payload layout, which is not the UTF-8 JSON <see cref="DistributedCacheService"/> writes, so
/// letting the two meet at one key would reproduce that failure at every key rather than at one
/// counter. With the keyspaces separated, an old-format entry is simply invisible to this service
/// (a clean miss) and a new-format entry is invisible to the old one, including while a rolling
/// deploy runs both. Old entries age out on their TTL; <see cref="RemoveByPrefixAsync"/> evicts both
/// keyspaces so long-lived records (24h idempotency responses) stay evictable through the migration
/// window.
/// </para>
/// <para>
/// Replica L1 staleness after an invalidation is bounded by the local expiration (30 seconds), not
/// by the eviction: only the evicting process's L1 is cleared. That is the accepted cost of the L1
/// hit rate, and it is the same order as the delayed re-invalidation the caching command decorator
/// already performs.
/// </para>
/// </remarks>
internal sealed partial class HybridCacheService(
    HybridCache hybrid,
    ILogger<HybridCacheService> logger,
    IConnectionMultiplexer? connectionMultiplexer = null,
    CacheKeyNamespace? keyNamespace = null) : ICacheService
{
    /// <summary>
    /// Segment separating this service's keyspace from the one <see cref="DistributedCacheService"/>
    /// writes. Applied inside the configured namespace, so a key reads <c>{prefix}hc:{key}</c>.
    /// </summary>
    internal const string KeyspaceSegment = "hc:";

    /// <summary>
    /// Default in-process (L1) lifetime, and the ceiling applied to every entry: a replica that never
    /// sees an invalidation still re-reads L2 within this window. Matches the default
    /// <c>AddCommonHybridCache</c> configures.
    /// </summary>
    internal static readonly TimeSpan LocalCacheDefault = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Read without a factory: <see cref="HybridCacheEntryFlags.DisableUnderlyingData"/> tells
    /// <see cref="HybridCache"/> not to invoke the factory and not to write anything, so a miss is a
    /// miss and stays one. An L2 hit is still promoted into L1, which is the whole point of the
    /// two-level cache.
    /// </summary>
    private static readonly HybridCacheEntryOptions ReadOnlyOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData,
    };

    /// <summary>
    /// The read leg of <see cref="IncrementAsync"/>: the read-only flags plus a full L1 bypass. See
    /// the remarks on <see cref="IncrementAsync"/> for why a counter must not touch L1.
    /// </summary>
    private static readonly HybridCacheEntryOptions CounterReadOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData
            | HybridCacheEntryFlags.DisableLocalCacheRead
            | HybridCacheEntryFlags.DisableLocalCacheWrite,
    };

    /// <summary>
    /// Namespace applied to every key so services sharing one cache instance cannot collide.
    /// Defaults to no prefix, which is correct for a host that owns its cache outright.
    /// </summary>
    private readonly CacheKeyNamespace _keys = keyNamespace ?? CacheKeyNamespace.None;

    /// <summary>Set once (via <see cref="Interlocked"/>) after the missing-multiplexer no-op is logged, so the steady state warns once rather than on every command.</summary>
    private int _noMultiplexerWarned;

    /// <inheritdoc />
    /// <remarks>
    /// Fail-soft, mirroring the fail-open posture of the caching decorators: the cache is an
    /// optimization, never the system of record. A fault (an entry this build can no longer
    /// deserialize, a transport blip) is logged at warning level and answered as a miss, and the
    /// offending entry is deleted best-effort so the next write repopulates it instead of the
    /// process failing the same read forever. Only <see cref="OperationCanceledException"/> is
    /// excluded, so a genuinely cancelled request still surfaces as cancellation.
    /// </remarks>
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = HybridKey(key);

        try
        {
            return await hybrid.GetOrCreateAsync(
                fullKey,
                static _ => ValueTask.FromResult<T?>(default),
                ReadOnlyOptions,
                tags: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailed(logger, key, ex);
            await SelfHealAsync(fullKey, cancellationToken).ConfigureAwait(false);
            return default;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The absolute expiration is the caller's, or the framework default when omitted (the same
    /// <see cref="CacheOptions.DefaultDuration"/> <see cref="DistributedCacheService"/> applies). The
    /// L1 copy is capped at the shorter of that TTL and <see cref="LocalCacheDefault"/>, so a
    /// long-lived entry does not sit in another replica's memory for its whole TTL after an
    /// invalidation this process cannot see.
    /// </remarks>
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default) =>
        hybrid.SetAsync(HybridKey(key), value, WriteOptions(expiration), tags: null, cancellationToken).AsTask();

    /// <inheritdoc />
    /// <remarks>Removes both levels: <see cref="HybridCache"/> clears this process's L1 copy along with the L2 entry.</remarks>
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        hybrid.RemoveAsync(HybridKey(key), cancellationToken).AsTask();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Runs the shared <see cref="RedisPrefixScanner"/> TWICE, over two disjoint patterns: this
    /// service's <c>{prefix}hc:{key}</c> keyspace and the legacy <c>{prefix}{key}</c> keyspace that
    /// <see cref="DistributedCacheService"/> wrote. A host switching to
    /// <c>AddCommonHybridCache</c> inherits live entries under the old shape (a 24h idempotency
    /// record outlives any deploy), and a prefix eviction that skipped them would leave a stale
    /// answer addressable for the rest of its TTL. The two patterns cannot overlap, so nothing is
    /// visited twice.
    /// </para>
    /// <para>
    /// Hybrid keys are deleted through <see cref="HybridCache.RemoveAsync(string, CancellationToken)"/>
    /// rather than by raw key: a raw delete would clear L2 and leave this process's own L1 copy
    /// serving the value it just invalidated. Legacy keys have no L1 to clear and are deleted raw.
    /// </para>
    /// <para>
    /// Without an <see cref="IConnectionMultiplexer"/> prefix eviction cannot run at all and is
    /// warned once, exactly as in <see cref="DistributedCacheService.RemoveByPrefixAsync"/>.
    /// </para>
    /// </remarks>
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (connectionMultiplexer is null)
        {
            // No multiplexer: prefix eviction cannot run, so cached entries expire on TTL alone. Warn
            // once so this dead invalidation is visible without flooding the log on every mutating
            // command.
            if (Interlocked.Exchange(ref _noMultiplexerWarned, 1) == 0)
                LogPrefixEvictionNoMultiplexer(logger);
            return;
        }

        // Resolved on the first legacy delete rather than up front: a host whose multiplexer reports
        // no scannable server, or that holds no legacy entries at all, never asks for a database.
        IDatabase? db = null;

        // Both passes share one multiplexer, so an empty server list is one condition, not two.
        var noServersLogged = false;
        void OnNoServers()
        {
            if (!noServersLogged)
            {
                noServersLogged = true;
                LogPrefixEvictionNoServer(logger, prefix);
            }
        }

        void OnServerFailed(string server, Exception ex) =>
            LogPrefixEvictionServerFailed(logger, server, prefix, ex);

        // This service's own keyspace: routed back through HybridCache so L1 goes with L2.
        await RedisPrefixScanner.RemoveMatchingAsync(
            connectionMultiplexer,
            $"{HybridKey(prefix)}*",
            key => hybrid.RemoveAsync(key.ToString(), cancellationToken).AsTask(),
            OnNoServers,
            OnServerFailed,
            cancellationToken).ConfigureAwait(false);

        // The legacy keyspace left behind by DistributedCacheService; raw deletes, nothing holds
        // these in an L1.
        await RedisPrefixScanner.RemoveMatchingAsync(
            connectionMultiplexer,
            $"{_keys.Qualify(prefix)}*",
            key => (db ??= connectionMultiplexer.GetDatabase()).KeyDeleteAsync(key),
            OnNoServers,
            OnServerFailed,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A read-modify-write, like <see cref="DistributedCacheService.IncrementAsync"/> and with the
    /// same honest limitation: concurrent increments can undercount. It is not routed through
    /// <see cref="GetAsync{T}"/> and <see cref="SetAsync{T}"/> because both legs must bypass L1
    /// (<see cref="HybridCacheEntryFlags.DisableLocalCacheRead"/> and
    /// <see cref="HybridCacheEntryFlags.DisableLocalCacheWrite"/>), which those members deliberately
    /// do not.
    /// </para>
    /// <para>
    /// The reason is that a counter is the one value in this cache whose correctness depends on
    /// every replica seeing the same number. An L1 copy would let a process read its own stale count
    /// for up to the local expiration and write it back, so a brute-force counter could be held near
    /// its starting value indefinitely by a steady stream of attempts against one replica: a
    /// security control silently weakened by a cache optimization. Keeping both legs on L2 preserves
    /// exactly today's semantics (ADR-029) and gives up only the L1 hit, which a counter that
    /// changes on every read was never going to benefit from.
    /// </para>
    /// <para>
    /// Faults are deliberately NOT swallowed here, unlike <see cref="GetAsync{T}"/>: a counter that
    /// silently reads as zero would reset the limit it exists to enforce, so the caller decides.
    /// </para>
    /// </remarks>
    public async Task<long> IncrementAsync(string key, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        var fullKey = HybridKey(key);

        var current = await hybrid.GetOrCreateAsync(
            fullKey,
            static _ => ValueTask.FromResult<long?>(null),
            CounterReadOptions,
            tags: null,
            cancellationToken).ConfigureAwait(false) ?? 0;

        var next = current + 1;

        await hybrid.SetAsync(
            fullKey,
            next,
            WriteOptions(expiration, HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableLocalCacheWrite),
            tags: null,
            cancellationToken).ConfigureAwait(false);

        return next;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overrides the interface default (get, stripe, double-check, factory, set) with
    /// <see cref="HybridCache"/>'s own implementation, which folds the same double-check and
    /// stampede protection into one call and additionally deduplicates concurrent callers before
    /// they reach L2. The guarantees the default member documents are unchanged: the factory result
    /// is cached unconditionally, and stampede protection is process-local, so several replicas can
    /// still each run the factory once.
    /// </remarks>
    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // The factory is passed as state rather than captured, so the delegate stays static and no
        // closure is allocated per call.
        return hybrid.GetOrCreateAsync(
            HybridKey(key),
            factory,
            static async (state, ct) => await state(ct).ConfigureAwait(false),
            WriteOptions(expiration),
            tags: null,
            cancellationToken).AsTask();
    }

    /// <summary>Qualifies a caller-supplied key into this service's disjoint keyspace.</summary>
    /// <param name="key">The caller-supplied key or prefix.</param>
    /// <returns>The fully qualified key, <c>{prefix}hc:{key}</c>.</returns>
    private string HybridKey(string key) => _keys.Qualify(string.Concat(KeyspaceSegment, key));

    /// <summary>
    /// Builds the entry options for a write: the caller's TTL (or the framework default), with the
    /// L1 copy capped at <see cref="LocalCacheDefault"/> or the TTL, whichever is shorter.
    /// </summary>
    /// <param name="expiration">The caller-supplied expiration, if any.</param>
    /// <param name="flags">Optional flags; <see langword="null"/> inherits the configured defaults.</param>
    /// <returns>The entry options to pass to <see cref="HybridCache"/>.</returns>
    private static HybridCacheEntryOptions WriteOptions(TimeSpan? expiration, HybridCacheEntryFlags? flags = null)
    {
        var ttl = expiration ?? CacheOptions.DefaultDuration;

        return new HybridCacheEntryOptions
        {
            Expiration = ttl,
            LocalCacheExpiration = ttl < LocalCacheDefault ? ttl : LocalCacheDefault,
            Flags = flags,
        };
    }

    /// <summary>
    /// Drops an entry that could not be read, so the next write repopulates it. Best-effort by
    /// definition: this runs while the cache is already misbehaving, so a failure here is logged and
    /// nothing more.
    /// </summary>
    /// <param name="fullKey">The fully qualified key to drop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task SelfHealAsync(string fullKey, CancellationToken cancellationToken)
    {
        try
        {
            await hybrid.RemoveAsync(fullKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSelfHealFailed(logger, fullKey, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Two-level cache read failed for key '{CacheKey}'; the read is treated as a miss and the entry is dropped so the next write repopulates it.")]
    private static partial void LogReadFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Two-level cache could not drop the unreadable entry '{CacheKey}'; it stays unreadable until its TTL expires.")]
    private static partial void LogSelfHealFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation is a no-op: no IConnectionMultiplexer is registered, so cached entries are bounded only by their TTL. Register a Redis client (AddRedisClient) to enable prefix eviction.")]
    private static partial void LogPrefixEvictionNoMultiplexer(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation skipped for prefix '{Prefix}': the connection multiplexer reports no non-replica servers.")]
    private static partial void LogPrefixEvictionNoServer(ILogger logger, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation failed on server '{Server}' for prefix '{Prefix}'; the remaining servers are still processed, so entries on this one are bounded only by their TTL.")]
    private static partial void LogPrefixEvictionServerFailed(ILogger logger, string server, string prefix, Exception exception);
}
