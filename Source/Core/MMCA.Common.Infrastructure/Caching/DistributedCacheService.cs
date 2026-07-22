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
/// uses SCAN to enumerate and delete matching keys. Otherwise prefix invalidation is a no-op and is
/// logged (once for the missing-multiplexer case, which is a steady state; every time for the
/// anomalous no-server case) so a silently-dead invalidation is observable instead of invisible.
/// </summary>
internal sealed partial class DistributedCacheService(
    IDistributedCache cache,
    ILogger<DistributedCacheService> logger,
    IConnectionMultiplexer? connectionMultiplexer = null) : ICacheService
{
    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await cache.GetAsync(key, cancellationToken).ConfigureAwait(false);

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

        return cache.SetAsync(key, bytes, CacheOptions.Create(expiration), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(key, cancellationToken);

    /// <summary>Keys per delete round trip during prefix invalidation.</summary>
    private const int DeleteBatchSize = 512;

    /// <summary>Set once (via <see cref="Interlocked"/>) after the missing-multiplexer no-op is logged, so the steady state warns once rather than on every command.</summary>
    private int _noMultiplexerWarned;

    /// <inheritdoc />
    /// <remarks>
    /// SCAN enumerates matching keys incrementally; deletes are issued in batches of
    /// <see cref="DeleteBatchSize"/> (one round trip per batch instead of one per key),
    /// keeping mutating commands from stalling on large invalidations.
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

        var server = connectionMultiplexer.GetServers().FirstOrDefault();
        if (server is null)
        {
            LogPrefixEvictionNoServer(logger, prefix);
            return;
        }

        var keys = server.KeysAsync(pattern: $"{prefix}*");
        var db = connectionMultiplexer.GetDatabase();
        var batch = new List<RedisKey>(DeleteBatchSize);

        await foreach (var key in keys.WithCancellation(cancellationToken))
        {
            batch.Add(key);
            if (batch.Count == DeleteBatchSize)
            {
                await db.KeyDeleteAsync([.. batch]).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await db.KeyDeleteAsync([.. batch]).ConfigureAwait(false);
    }

    private static T Deserialize<T>(byte[] bytes)
        => JsonSerializer.Deserialize<T>(bytes)!;

    private static byte[] Serialize<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation is a no-op: no IConnectionMultiplexer is registered, so cached entries are bounded only by their TTL. Register a Redis client (AddRedisClient) to enable prefix eviction.")]
    private static partial void LogPrefixEvictionNoMultiplexer(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix-based cache invalidation skipped for prefix '{Prefix}': the connection multiplexer reports no servers.")]
    private static partial void LogPrefixEvictionNoServer(ILogger logger, string prefix);
}
