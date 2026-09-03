using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Cache backed by <see cref="IDistributedCache"/> (e.g., Redis, SQL Server).
/// Serializes values as UTF-8 JSON via <see cref="System.Text.Json.JsonSerializer"/>.
/// When an <see cref="IConnectionMultiplexer"/> is available (Redis), <see cref="RemoveByPrefixAsync"/>
/// uses SCAN on every non-replica server to enumerate matching keys and deletes them one key per
/// command. Otherwise prefix invalidation is a no-op and is logged (once for the missing-multiplexer
/// case, which is a steady state; every time for the anomalous no-server case) so a silently-dead
/// invalidation is observable instead of invisible.
/// </summary>
internal sealed partial class DistributedCacheService(
    IDistributedCache cache,
    ILogger<DistributedCacheService> logger,
    IConnectionMultiplexer? connectionMultiplexer = null,
    CacheKeyNamespace? keyNamespace = null,
    IOptions<CacheSettings>? cacheSettings = null) : ICacheService
{
    /// <summary>
    /// Namespace applied to every key so services sharing one cache instance cannot collide.
    /// Defaults to no prefix, which is correct for a host that owns its cache outright.
    /// </summary>
    private readonly CacheKeyNamespace _keys = keyNamespace ?? CacheKeyNamespace.None;

    /// <summary>
    /// Bound <c>Cache</c> section, or the framework defaults when a host builds this service without
    /// one (direct construction in tests). The default reproduces
    /// <see cref="CacheOptions.DefaultDuration"/> exactly.
    /// </summary>
    private readonly CacheSettings _settings = cacheSettings?.Value ?? new CacheSettings();

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await cache.GetAsync(_keys.Qualify(key), cancellationToken).ConfigureAwait(false);

        return bytes is null ? default : Deserialize<T>(bytes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A caller that supplies no expiration gets <see cref="CacheSettings.DefaultDuration"/>, which
    /// defaults to <see cref="CacheOptions.DefaultDuration"/>, so an unconfigured host writes the
    /// same TTL it always did.
    /// </remarks>
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = Serialize(value);

        return cache.SetAsync(
            _keys.Qualify(key),
            bytes,
            CacheOptions.Create(expiration ?? _settings.DefaultDuration),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(_keys.Qualify(key), cancellationToken);

    /// <summary>Set once (via <see cref="Interlocked"/>) after the missing-multiplexer no-op is logged, so the steady state warns once rather than on every command.</summary>
    private int _noMultiplexerWarned;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Every non-replica server is scanned, not just the first one the multiplexer reports. Keys
    /// are distributed across primaries, so scanning one server leaves the entries held by the
    /// others alive until their TTL expires. Replicas are skipped: their keyspace mirrors a
    /// primary already scanned, and a delete against a replica is rejected.
    /// </para>
    /// <para>
    /// SCAN enumerates matching keys incrementally and each key is deleted by its own single-key
    /// command. A multi-key DEL cannot span hash slots under Redis cluster policy, and
    /// StackExchange.Redis rejects a cross-slot multi-key command by throwing rather than
    /// under-deleting, which would fault the whole invalidation. Round trips still stay bounded:
    /// a fixed number of single-key deletes are in flight at a time and awaited as one group.
    /// </para>
    /// <para>
    /// Each server is scanned inside its own try/catch, so one unreachable or failing server is
    /// logged and skipped instead of aborting invalidation on the servers that are healthy.
    /// </para>
    /// <para>
    /// The scan itself lives in <see cref="RedisPrefixScanner"/>, shared with
    /// <see cref="HybridCacheService"/>. This method supplies the raw <c>KeyDeleteAsync</c> as the
    /// per-key delete and keeps the log messages, which name the caller-supplied prefix.
    /// </para>
    /// </remarks>
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (connectionMultiplexer is null)
        {
            // No multiplexer (e.g. a SQL-Server-backed IDistributedCache, or Redis registered without
            // AddRedisClient): prefix eviction cannot run, so cached entries expire on TTL alone. Warn once
            // so this dead invalidation is visible without flooding the log on every mutating command.
            if (Interlocked.Exchange(ref _noMultiplexerWarned, 1) == 0)
                LogPrefixEvictionNoMultiplexer(logger);
            return;
        }

        // Resolved on the first delete rather than up front: a host whose multiplexer reports no
        // scannable server never asks for a database at all.
        IDatabase? db = null;

        await RedisPrefixScanner.RemoveMatchingAsync(
            connectionMultiplexer,
            $"{_keys.Qualify(prefix)}*",
            key => (db ??= connectionMultiplexer.GetDatabase()).KeyDeleteAsync(key),
            () => LogPrefixEvictionNoServer(logger, prefix),
            (server, ex) => LogPrefixEvictionServerFailed(logger, server, prefix, ex),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately goes through <see cref="IDistributedCache"/> rather than Redis <c>INCR</c>.
    /// <para>
    /// <c>INCR</c> would be atomic, which is what this member was added for, but it writes a Redis
    /// <b>string</b> while <c>StackExchangeRedisCache</c> stores every entry as a Redis <b>hash</b>
    /// (<c>absexp</c> / <c>sldexp</c> / <c>data</c> fields, read back with <c>HMGET</c>). Mixing the
    /// two at one key means the next read of that counter fails with <c>WRONGTYPE</c>, which
    /// surfaces as a 500 on whatever endpoint owns the counter: registration and login in the
    /// ADR-029 case. The counter has to live in the same storage format as the reads that consult
    /// it.
    /// </para>
    /// <para>
    /// So this is a read-modify-write and can undercount under genuinely concurrent increments. For
    /// the brute-force and rate-limit counters this backs, an occasional lost increment is a far
    /// smaller problem than the counter being unreadable. Making it atomic again means either
    /// running the whole read-modify-write in one Lua script against the hash layout, or moving
    /// counters out of <see cref="IDistributedCache"/> entirely so both sides speak Redis strings.
    /// </para>
    /// </remarks>
    public async Task<long> IncrementAsync(string key, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync<long?>(key, cancellationToken).ConfigureAwait(false) ?? 0;
        var next = current + 1;
        await SetAsync(key, next, expiration, cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static T Deserialize<T>(byte[] bytes)
        => JsonSerializer.Deserialize<T>(bytes)!;

    private static byte[] Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation is a no-op: no IConnectionMultiplexer is registered, so cached entries are bounded only by their TTL. Register a Redis client (AddRedisClient) to enable prefix eviction.")]
    private static partial void LogPrefixEvictionNoMultiplexer(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation skipped for prefix '{Prefix}': the connection multiplexer reports no non-replica servers.")]
    private static partial void LogPrefixEvictionNoServer(ILogger logger, string prefix);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation failed on server '{Server}' for prefix '{Prefix}'; the remaining servers are still processed, so entries on this one are bounded only by their TTL.")]
    private static partial void LogPrefixEvictionServerFailed(ILogger logger, string server, string prefix, Exception exception);
}
