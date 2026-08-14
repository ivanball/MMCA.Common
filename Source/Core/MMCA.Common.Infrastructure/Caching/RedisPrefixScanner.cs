using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Prefix eviction over a real Redis: SCAN every non-replica server for a key pattern and delete
/// every match. Shared by <see cref="DistributedCacheService"/> and <see cref="HybridCacheService"/>
/// so both evict through one implementation, and so the Redis-tier tests that cover one cover both.
/// </summary>
/// <remarks>
/// <para>
/// The delete is a caller-supplied callback rather than a fixed <c>KeyDeleteAsync</c>: the two
/// services have to remove a key differently. <see cref="DistributedCacheService"/> deletes the raw
/// Redis key, while <see cref="HybridCacheService"/> routes the delete back through
/// <c>HybridCache.RemoveAsync</c> so the caller's in-process L1 copy is evicted too, not just the
/// L2 entry a raw delete would clear.
/// </para>
/// <para>
/// Logging stays with the caller (two <see cref="Action"/> hooks): each service owns its own
/// <c>LoggerMessage</c> definitions and its own notion of the prefix being evicted, so the message
/// text and its structured fields do not move into a shared helper.
/// </para>
/// </remarks>
internal static class RedisPrefixScanner
{
    /// <summary>Single-key deletes issued and awaited as one group during prefix invalidation.</summary>
    private const int DeleteBatchSize = 512;

    /// <summary>
    /// Scans every non-replica server for <paramref name="pattern"/> and deletes each matching key
    /// through <paramref name="deleteAsync"/>.
    /// </summary>
    /// <param name="connectionMultiplexer">The connected Redis multiplexer.</param>
    /// <param name="pattern">The glob pattern to match, already namespace-qualified by the caller.</param>
    /// <param name="deleteAsync">Removes one matched key; awaited in batches.</param>
    /// <param name="onNoServers">Invoked when the multiplexer reports no non-replica server.</param>
    /// <param name="onServerFailed">Invoked with the server description and the fault when one server fails.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// <para>
    /// Every non-replica server is scanned, not just the first one the multiplexer reports. Keys
    /// are distributed across primaries, so scanning one server leaves the entries held by the
    /// others alive until their TTL expires. Replicas are skipped: their keyspace mirrors a
    /// primary already scanned, and a delete against a replica is rejected.
    /// </para>
    /// <para>
    /// Each server is scanned inside its own try/catch, so one unreachable or failing server is
    /// logged and skipped instead of aborting invalidation on the servers that are healthy.
    /// Cancellation is deliberately not caught.
    /// </para>
    /// </remarks>
    internal static async Task RemoveMatchingAsync(
        IConnectionMultiplexer connectionMultiplexer,
        string pattern,
        Func<RedisKey, Task> deleteAsync,
        Action onNoServers,
        Action<string, Exception> onServerFailed,
        CancellationToken cancellationToken)
    {
        // Replicas mirror a primary's keyspace, so scanning them is redundant, and a DEL against a
        // replica fails outright. Everything else is scanned: a key that lives on another primary
        // is invisible to the first server's SCAN and would survive the invalidation.
        IServer[] servers = [.. connectionMultiplexer.GetServers().Where(s => !s.IsReplica)];
        if (servers.Length == 0)
        {
            onNoServers();
            return;
        }

        foreach (var server in servers)
        {
            try
            {
                await ScanAndDeleteAsync(server, pattern, deleteAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or RedisCommandException or TimeoutException)
            {
                // Best-effort per server: a transport or command fault on one node must not leave the
                // remaining nodes un-invalidated. Cancellation is deliberately not caught here.
                onServerFailed(Describe(server), ex);
            }
        }
    }

    /// <summary>Scans one server for the pattern and deletes every match, one key per command.</summary>
    /// <param name="server">The server to scan.</param>
    /// <param name="pattern">The glob pattern to match.</param>
    /// <param name="deleteAsync">Removes one matched key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task ScanAndDeleteAsync(
        IServer server,
        string pattern,
        Func<RedisKey, Task> deleteAsync,
        CancellationToken cancellationToken)
    {
        // Deletes go out one key at a time. Under Redis cluster policy a multi-key DEL must not span
        // hash slots, and StackExchange.Redis answers a cross-slot multi-key command by throwing, so
        // batching the keys of a prefix (which hash to arbitrary slots) would fault the invalidation
        // instead of speeding it up. Single-key deletes are always slot-safe; the round-trip count
        // stays bounded by keeping a group of them in flight and awaiting the group.
        var pending = new List<Task>(DeleteBatchSize);

        await foreach (var key in server.KeysAsync(pattern: pattern)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            pending.Add(deleteAsync(key));
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
}
