using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using MMCA.Common.Application.Interfaces;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Cache backed by <see cref="IDistributedCache"/> (e.g., Redis, SQL Server).
/// Serializes values as UTF-8 JSON via <see cref="System.Text.Json.JsonSerializer"/>.
/// When an <see cref="IConnectionMultiplexer"/> is available (Redis), <see cref="RemoveByPrefixAsync"/>
/// uses SCAN to enumerate and delete matching keys. Otherwise it is a no-op.
/// </summary>
internal sealed class DistributedCacheService(
    IDistributedCache cache,
    IConnectionMultiplexer? connectionMultiplexer = null) : ICacheService
{
    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await cache.GetAsync(key, cancellationToken);

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

    /// <inheritdoc />
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        if (connectionMultiplexer is null)
            return;

        var server = connectionMultiplexer.GetServers().FirstOrDefault();
        if (server is null)
            return;

        var keys = server.KeysAsync(pattern: $"{prefix}*");
        var db = connectionMultiplexer.GetDatabase();

        await foreach (var key in keys.WithCancellation(cancellationToken))
        {
            await db.KeyDeleteAsync(key).ConfigureAwait(false);
        }
    }

    private static T Deserialize<T>(byte[] bytes)
        => JsonSerializer.Deserialize<T>(bytes)!;

    private static byte[] Serialize<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value);
        return buffer.WrittenSpan.ToArray();
    }
}
