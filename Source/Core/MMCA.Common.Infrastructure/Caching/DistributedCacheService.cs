using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
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
    CacheKeyNamespace? keyNamespace = null) : ICacheService
{
    /// <summary>Single-key deletes issued and awaited as one group during prefix invalidation.</summary>
    private const int DeleteBatchSize = 512;

    /// <summary>
    /// Namespace applied to every key so services sharing one cache instance cannot collide.
    /// Defaults to no prefix, which is correct for a host that owns its cache outright.
    /// </summary>
    private readonly CacheKeyNamespace _keys = keyNamespace ?? CacheKeyNamespace.None;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await cache.GetAsync(_keys.Qualify(key), cancellationToken).ConfigureAwait(false);

        return bytes is null ? default : Deserialize<T>(bytes);
    }

    /// <inheritdoc />
    public Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes = Serialize(value);

        return cache.SetAsync(_keys.Qualify(key), bytes, CacheOptions.Create(expiration), cancellationToken);
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
    /// up to <see cref="DeleteBatchSize"/> single-key deletes are in flight at a time and awaited
    /// as one group.
    /// </para>
    /// <para>
    /// Each server is scanned inside its own try/catch, so one unreachable or failing server is
    /// logged and skipped instead of aborting invalidation on the servers that are healthy.
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

        // Replicas mirror a primary's keyspace, so scanning them is redundant, and a DEL against a
        // replica fails outright. Everything else is scanned: a key that lives on another primary
        // is invisible to the first server's SCAN and would survive the invalidation.
        IServer[] servers = [.. connectionMultiplexer.GetServers().Where(s => !s.IsReplica)];
        if (servers.Length == 0)
        {
            LogPrefixEvictionNoServer(logger, prefix);
            return;
        }

        var pattern = $"{_keys.Qualify(prefix)}*";
        var db = connectionMultiplexer.GetDatabase();

        foreach (var server in servers)
        {
            try
            {
                await ScanAndDeleteAsync(db, server, pattern, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or RedisCommandException or TimeoutException)
            {
                // Best-effort per server: a transport or command fault on one node must not leave the
                // remaining nodes un-invalidated. Cancellation is deliberately not caught here.
                LogPrefixEvictionServerFailed(logger, Describe(server), prefix, ex);
            }
        }
    }

    /// <summary>Scans one server for the pattern and deletes every match, one key per command.</summary>
    private static async Task ScanAndDeleteAsync(
        IDatabase db,
        IServer server,
        string pattern,
        CancellationToken cancellationToken)
    {
        // Deletes go out one key at a time. Under Redis cluster policy a multi-key DEL must not span
        // hash slots, and StackExchange.Redis answers a cross-slot multi-key command by throwing, so
        // batching the keys of a prefix (which hash to arbitrary slots) would fault the invalidation
        // instead of speeding it up. Single-key deletes are always slot-safe; the round-trip count
        // stays bounded by keeping a group of them in flight and awaiting the group.
        var pending = new List<Task<bool>>(DeleteBatchSize);

        await foreach (var key in server.KeysAsync(pattern: pattern)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            pending.Add(db.KeyDeleteAsync(key));
            if (pending.Count == DeleteBatchSize)
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
                pending.Clear();
            }
        }

        if (pending.Count > 0)
            await Task.WhenAll(pending).ConfigureAwait(false);
    }

    /// <summary>Stable identifier for a server in log output, tolerating an unknown endpoint.</summary>
    private static string Describe(IServer server) =>
        server.EndPoint?.ToString() ?? "unknown";

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
