using System.Diagnostics;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MMCA.Common.API.RateLimiting;

/// <summary>
/// A <see cref="RateLimiter"/> that counts a fixed one-minute window in Redis, so every instance
/// behind a load balancer shares one allowance per partition instead of each getting its own.
/// Selected by <c>RateLimitingSettings.Distributed</c> for the global limiter and the "UserPolicy"
/// limiter; when no <see cref="IConnectionMultiplexer"/> is registered the caller falls back to the
/// in-memory limiters instead.
/// <para>
/// <b>Layering:</b> this lives in MMCA.Common.API rather than behind an abstraction implemented in
/// Infrastructure because API already references Infrastructure (which owns the
/// StackExchange.Redis dependency), so using the client here adds no new dependency edge and
/// breaks no rule in <c>MMCA.Common.LayerEnforcement.targets</c>. An extra interface plus an
/// Infrastructure implementation would buy nothing but indirection: the limiter is a presentation
/// concern that only the rate-limiting middleware constructs.
/// </para>
/// <para>
/// <b>Storage:</b> one key per partition per window, <c>rl:{partitionKey}:{unixMinute}</c>,
/// incremented with <c>INCR</c> and given a TTL slightly longer than the window on the increment
/// that creates it. Keys expire on their own, so nothing has to sweep them, and a window rollover
/// is a new key rather than a reset. The counter is not transactional with the permit decision
/// (INCR then compare), which can let a burst arriving in the same instant overshoot the limit
/// slightly; that is the accepted trade for one round trip per request.
/// </para>
/// <para>
/// <b>Fail open:</b> any Redis fault permits the request and logs at warning level, at most once
/// per window across the process. Rate limiting protects capacity; it must never become the reason
/// an otherwise healthy request is rejected.
/// </para>
/// </summary>
public sealed partial class RedisFixedWindowRateLimiter : RateLimiter
{
    /// <summary>
    /// The window this process last logged a Redis fault for, so a dead Redis produces one warning
    /// a minute rather than one per request. Static (rather than per instance) because a fault is a
    /// property of the connection, which every partition shares.
    /// </summary>
    private static long _lastLoggedFailureWindow = -1;

    private readonly IConnectionMultiplexer _connection;
    private readonly string _partitionKey;
    private readonly int _permitLimit;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    private long _lastUsedTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisFixedWindowRateLimiter"/> class.
    /// </summary>
    /// <param name="connection">The shared Redis connection multiplexer.</param>
    /// <param name="partitionKey">
    /// The partition this limiter counts, already scoped by the caller (e.g. <c>global:alice</c>)
    /// so two policies limiting the same user never share one counter.
    /// </param>
    /// <param name="permitLimit">Permits allowed per one-minute window.</param>
    /// <param name="logger">Logger for the fail-open warning.</param>
    /// <param name="timeProvider">
    /// Clock used to compute the window stamp. Defaults to <see cref="TimeProvider.System"/>;
    /// tests substitute it to pin a window.
    /// </param>
    public RedisFixedWindowRateLimiter(
        IConnectionMultiplexer connection,
        string partitionKey,
        int permitLimit,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _partitionKey = partitionKey;
        _permitLimit = permitLimit;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// How long this partition has gone unused, so the owning
    /// <see cref="PartitionedRateLimiter{TResource}"/> can evict it. Reported rather than left
    /// <see langword="null"/> because partition keys embed user identity: never reporting idleness
    /// would grow the partition table with one entry per user seen since start-up.
    /// </summary>
    public override TimeSpan? IdleDuration =>
        Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUsedTimestamp));

    /// <summary>
    /// Returns <see langword="null"/>: the counter lives in Redis, and reporting it would cost a
    /// round trip per call for a diagnostic the middleware does not require.
    /// </summary>
    /// <returns>Always <see langword="null"/>.</returns>
    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>
    /// Synchronous acquisition always permits. The ASP.NET Core rate-limiting middleware uses the
    /// asynchronous path exclusively, so this exists only to satisfy the base contract, and
    /// blocking a request thread on a Redis round trip to serve it would be strictly worse than
    /// the fail-open posture the whole limiter already takes on a Redis fault.
    /// </summary>
    /// <param name="permitCount">The permits requested.</param>
    /// <returns>An acquired lease.</returns>
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());
        return RedisRateLimitLease.Acquired;
    }

    /// <inheritdoc />
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastUsedTimestamp, Stopwatch.GetTimestamp());

        if (permitCount > _permitLimit)
        {
            return RedisRateLimitLease.Rejected;
        }

        var window = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;
        var key = string.Create(CultureInfo.InvariantCulture, $"rl:{_partitionKey}:{window}");

        try
        {
            var database = _connection.GetDatabase();
            var count = await database.StringIncrementAsync(key, permitCount).ConfigureAwait(false);

            if (count <= permitCount)
            {
                // First increment of this window created the key, so give it a TTL. The skew past
                // the window length covers clock drift between instances: an early-expiring key
                // would hand the partition a fresh allowance inside the same window.
                await database.KeyExpireAsync(key, TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            }

            return count <= _permitLimit ? RedisRateLimitLease.Acquired : RedisRateLimitLease.Rejected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Interlocked.Exchange(ref _lastLoggedFailureWindow, window) != window)
            {
                LogRedisUnavailable(_logger, _partitionKey, ex);
            }

            return RedisRateLimitLease.Acquired;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Distributed rate limiting is failing open for partition '{PartitionKey}': the Redis counter is unreachable, so requests are permitted uncounted until it recovers")]
    private static partial void LogRedisUnavailable(ILogger logger, string partitionKey, Exception exception);
}

/// <summary>
/// The two leases <see cref="RedisFixedWindowRateLimiter"/> hands out. Both are stateless and
/// carry no metadata, so one shared instance of each serves every request rather than allocating
/// a lease per call.
/// </summary>
internal sealed class RedisRateLimitLease : RateLimitLease
{
    /// <summary>The lease returned when the request is permitted.</summary>
    internal static readonly RedisRateLimitLease Acquired = new(isAcquired: true);

    /// <summary>The lease returned when the window's allowance is exhausted.</summary>
    internal static readonly RedisRateLimitLease Rejected = new(isAcquired: false);

    private RedisRateLimitLease(bool isAcquired) => IsAcquired = isAcquired;

    /// <inheritdoc />
    public override bool IsAcquired { get; }

    /// <inheritdoc />
    public override IEnumerable<string> MetadataNames => [];

    /// <inheritdoc />
    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        metadata = null;
        return false;
    }
}
