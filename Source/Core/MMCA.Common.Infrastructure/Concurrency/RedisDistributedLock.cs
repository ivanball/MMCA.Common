using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Concurrency;

/// <summary>
/// Redis-backed <see cref="IDistributedLock"/>: the standard <c>SET key token NX PX ttl</c> lock.
/// <para>
/// Acquisition is a single atomic <c>SET</c> with <see cref="When.NotExists"/> and an expiry, so
/// exactly one replica can win a key, and a holder that crashes releases it by expiry rather than
/// wedging it forever. Release runs a compare-and-delete script against the owner token, so a
/// caller whose TTL already lapsed cannot delete the entry a different replica now owns.
/// </para>
/// </summary>
/// <remarks>
/// Single-instance semantics deliberately: this is the one-Redis lock, not Redlock. It inherits
/// Redis's failover behavior, which is why <see cref="IDistributedLock"/> is documented as
/// best-effort.
/// </remarks>
internal sealed partial class RedisDistributedLock(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisDistributedLock> logger,
    CacheKeyNamespace? keyNamespace = null) : IDistributedLock
{
    /// <summary>Keyspace prefix, so locks cannot collide with cache entries in a shared instance.</summary>
    private const string KeyPrefix = "lock:";

    /// <summary>
    /// Compare-and-delete. Deleting without the comparison would let a caller whose lock already
    /// expired free the next holder's lock, which is exactly the double-execution this prevents.
    /// </summary>
    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    /// <summary>Gap between acquisition attempts while waiting for a holder to release.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly CacheKeyNamespace _keys = keyNamespace ?? CacheKeyNamespace.None;

    /// <inheritdoc />
    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string key,
        TimeSpan ttl,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);

        RedisKey redisKey = _keys.Qualify(string.Concat(KeyPrefix, key));

        // Random per acquisition: the release script matches on it, which is what makes a release
        // owner-scoped instead of "delete whatever is there now".
        RedisValue token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        IDatabase database = connectionMultiplexer.GetDatabase();
        var startedAt = Stopwatch.GetTimestamp();

        while (true)
        {
            var acquired = await database
                .StringSetAsync(redisKey, token, ttl, keepTtl: false, When.NotExists, CommandFlags.None)
                .ConfigureAwait(false);

            if (acquired)
            {
                return new RedisLockHandle(database, redisKey, token, logger);
            }

            if (Stopwatch.GetElapsedTime(startedAt) >= wait)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Distributed lock '{Key}' had already expired when its holder released it: the guarded section ran longer than the lock's time-to-live and was not exclusive for all of it.")]
    private static partial void LogLockAlreadyExpired(ILogger logger, string key);

    /// <summary>Releases exactly the acquisition it was created for, once.</summary>
    private sealed class RedisLockHandle(
        IDatabase database,
        RedisKey key,
        RedisValue token,
        ILogger logger) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            RedisResult result = await database
                .ScriptEvaluateAsync(ReleaseScript, [key], [token], CommandFlags.None)
                .ConfigureAwait(false);

            // 0 means the key was gone or held by someone else, i.e. this holder's TTL lapsed
            // mid-section. Nothing to release, but worth surfacing: the section was not exclusive.
            if ((long)result == 0)
            {
                LogLockAlreadyExpired(logger, key.ToString());
            }
        }
    }
}
