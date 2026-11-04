using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Prefix invalidation for <see cref="HybridCacheService"/> without walking the keyspace: every
/// invalidation boundary of a key (the empty root, then each prefix ending at a <c>:</c>) carries a
/// generation token, and the tokens in force are folded into the key the entry is stored under.
/// Invalidating a prefix replaces its token, so every entry written under the old token simply
/// stops being addressed (a miss on every replica) and expires on its own TTL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>HybridCache.RemoveByTagAsync</c>.</b> In Microsoft.Extensions.Caching.Hybrid
/// 10.11.0 a tag invalidation stores a timestamp under <c>__MSFT_HCT__{tag}</c> in L2, but each
/// process reads that timestamp once, the first time it meets the tag, and keeps it for its
/// lifetime (<c>DefaultHybridCache._tagInvalidationTimes</c>, populated by
/// <c>SafeReadTagInvalidationAsync</c> and never refreshed). Another replica that has already seen
/// the tag therefore never learns of the invalidation, and keeps accepting the L2 payload as valid
/// even after its L1 copy expires. The Redis-tier test
/// <c>HybridCacheCrossInstanceInvalidationTests</c> pins the property this class provides instead.
/// </para>
/// <para>
/// <b>Tokens, not counters.</b> A token is a fresh random value, not an <c>INCR</c> result, so a
/// generation key that is lost (evicted, or a Redis restart without persistence) can never come back
/// at a value an older entry was written under. Any fresh value invalidates, so concurrent
/// invalidations need no atomicity: whichever write lands last wins and both invalidated.
/// </para>
/// <para>
/// <b>Staleness bound.</b> With Redis, each process caches a token it read for at most the local
/// cache duration (<c>Cache:LocalCacheDuration</c>, 30 seconds by default), the same bound the L1
/// copies already carry. The invalidating process adopts its new token at once, and a token read
/// that was already in flight cannot overwrite it. A generation key is written only when a prefix
/// is invalidated and carries no TTL, so Redis holds one short string per distinct invalidated
/// prefix; a boundary nobody invalidated costs a cached miss, never a write.
/// </para>
/// <para>
/// <b>Without a multiplexer</b> the tokens live in this process only, which is exact for a
/// single-process host and makes an invalidation reach no other replica; that is warned once.
/// </para>
/// <para>
/// <b>Redis outage.</b> A token read that fails answers with the last token this process knew for
/// the boundary (kept for <see cref="RetentionMultiplier"/> local durations past its expiry for
/// exactly this), or, for a boundary it never saw or has long forgotten, a fresh random token that
/// nothing was written under. Never the empty baseline: an L2 entry written before the prefix's
/// first invalidation still sits under it. Either answer is remembered for one local duration, so
/// an outage costs at most one failed round trip per boundary per local duration (concurrent reads
/// of one boundary share that round trip), and an L1 hit keeps answering in between instead of
/// every call waiting out the Redis timeout. The bound that results: in a full outage only L1 can
/// answer, so a served value is never older than the local cache duration, as in steady state; in a
/// partial outage (L2 reads succeed while generation reads fail) an invalidation made by ANOTHER
/// replica is missed for up to the outage's length plus one local duration. An invalidation made by
/// this process during the outage still takes effect here at once, since the new token is adopted
/// locally before the Redis write is attempted.
/// </para>
/// </remarks>
internal sealed partial class CachePrefixGenerations
{
    /// <summary>
    /// Segment the generation keys live under, inside the configured namespace
    /// (<c>{prefix}hcg:{invalidation prefix}</c>). Disjoint from <see cref="HybridCacheService.KeyspaceSegment"/>,
    /// so no cache entry can ever be read as a generation or the reverse.
    /// </summary>
    internal const string KeyspaceSegment = "hcg:";

    /// <summary>Separates the caller's key from the folded tokens in a stored key.</summary>
    internal const char SuffixSeparator = '|';

    /// <summary>
    /// How many local durations a remembered token is kept past its expiry, as the last-known
    /// answer a failed read falls back to. Five minutes at the default local duration.
    /// </summary>
    internal const int RetentionMultiplier = 10;

    /// <summary>Separates one folded token from the next.</summary>
    private const char TokenSeparator = '.';

    /// <summary>Remembered tokens are swept for long-expired entries once per this many inserts.</summary>
    private const int SweepInterval = 1024;

    private readonly IConnectionMultiplexer? _redis;
    private readonly CacheKeyNamespace _keys;
    private readonly long _localDurationMs;
    private readonly ILogger _logger;

    /// <summary>
    /// Tokens this process knows, each reused without a Redis read until <see cref="RememberedToken.ExpiresAt"/>
    /// and kept as the last-known fallback for <see cref="RetentionMultiplier"/> local durations after.
    /// </summary>
    private readonly ConcurrentDictionary<string, RememberedToken> _remembered = new(StringComparer.Ordinal);

    /// <summary>The token read in flight per boundary, so concurrent readers share one round trip.</summary>
    private readonly ConcurrentDictionary<string, Task<string>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>The authoritative tokens when there is no Redis: only invalidated prefixes appear.</summary>
    private readonly ConcurrentDictionary<string, string> _processLocal = new(StringComparer.Ordinal);

    /// <summary>Monotonic stamp on every remembered token, so an older read never overwrites a newer token.</summary>
    private long _version;

    private int _insertsSinceSweep;
    private int _processLocalWarned;

    /// <summary>Initializes a new instance of the <see cref="CachePrefixGenerations"/> class.</summary>
    /// <param name="redis">The shared server, or <see langword="null"/> for process-local generations.</param>
    /// <param name="keys">The namespace applied to the generation keys, the same one the entries use.</param>
    /// <param name="localDuration">How long a token read from Redis may be reused before it is re-read.</param>
    /// <param name="logger">Receives the warnings this class raises.</param>
    public CachePrefixGenerations(
        IConnectionMultiplexer? redis,
        CacheKeyNamespace keys,
        TimeSpan localDuration,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _keys = keys;
        _localDurationMs = (long)Math.Max(0, localDuration.TotalMilliseconds);
        _logger = logger;
    }

    /// <summary>
    /// Builds the suffix that folds the tokens in force for <paramref name="key"/>'s boundaries
    /// into its stored key. Empty when no boundary of the key was ever invalidated, so a key keeps
    /// its plain shape until something invalidates it.
    /// </summary>
    /// <param name="key">The caller-supplied key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The suffix to append, or an empty string.</returns>
    public async ValueTask<string> SuffixAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var boundaries = Boundaries(key);
        var tokens = new string[boundaries.Count];

        if (_redis is null)
        {
            for (var i = 0; i < boundaries.Count; i++)
            {
                tokens[i] = _processLocal.TryGetValue(boundaries[i], out var token) ? token : string.Empty;
            }

            return Compose(tokens);
        }

        // Unknown or expired tokens are read concurrently: StackExchange.Redis pipelines them over
        // one connection, so a key with several boundaries costs one round trip, not several.
        // Deliberately one GET per key rather than an MGET, which a clustered server refuses when the
        // keys hash to different slots.
        var now = Environment.TickCount64;
        List<(int Index, Task<string> Read)>? pending = null;

        for (var i = 0; i < boundaries.Count; i++)
        {
            if (_remembered.TryGetValue(boundaries[i], out var remembered) && now < remembered.ExpiresAt)
            {
                tokens[i] = remembered.Token;
            }
            else
            {
                (pending ??= []).Add((i, ReadShared(boundaries[i])));
            }
        }

        if (pending is not null)
        {
            // A shared read never faults on a Redis failure: it already resolved to its fallback.
            foreach (var (index, read) in pending)
            {
                tokens[index] = await read.ConfigureAwait(false);
            }
        }

        return Compose(tokens);
    }

    /// <summary>
    /// Replaces the token of <paramref name="prefix"/>, which ends every entry stored under a key
    /// that starts with it. This process adopts the new token at once; other processes adopt it
    /// within the local cache duration.
    /// </summary>
    /// <param name="prefix">An invalidation boundary: empty, or ending in <c>:</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// A failed Redis write propagates after the local adoption, so this process has already
    /// stopped serving the prefix; the caching command decorator logs it and never fails the
    /// committed command over it.
    /// </remarks>
    public async Task InvalidateAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        cancellationToken.ThrowIfCancellationRequested();

        var token = NewToken();

        if (_redis is null)
        {
            _processLocal[prefix] = token;

            if (Interlocked.Exchange(ref _processLocalWarned, 1) == 0)
                LogProcessLocalInvalidation(_logger);

            return;
        }

        // Local first: even if the write below fails, this process stops serving the prefix, and the
        // new token is the last-known fallback should Redis stay unreachable.
        Remember(prefix, token, readStartedAt: long.MaxValue);
        await _redis.GetDatabase().StringSetAsync(GenerationKey(prefix), token).ConfigureAwait(false);
    }

    /// <summary>
    /// The invalidation boundaries of a key: the empty root, then every prefix that ends at a
    /// <c>:</c>. For <c>A:B:C</c> that is <c>""</c>, <c>A:</c> and <c>A:B:</c>.
    /// </summary>
    /// <param name="key">The caller-supplied key.</param>
    /// <returns>The boundaries, shortest first.</returns>
    internal static List<string> Boundaries(string key)
    {
        List<string> boundaries = [string.Empty];

        for (var end = key.IndexOf(':', StringComparison.Ordinal); end >= 0; end = key.IndexOf(':', end + 1))
        {
            boundaries.Add(key[..(end + 1)]);
        }

        return boundaries;
    }

    /// <summary>Folds the non-empty tokens into one suffix.</summary>
    /// <param name="tokens">The tokens in force, one per boundary.</param>
    /// <returns>The suffix, or an empty string when no boundary carries a token.</returns>
    private static string Compose(string[] tokens)
    {
        // Tokens are random and never reused, so their order and position need no encoding: a
        // different set of tokens is always a different suffix.
        var inForce = tokens.Where(t => t.Length > 0).ToArray();
        return inForce.Length == 0
            ? string.Empty
            : string.Concat(SuffixSeparator.ToString(), string.Join(TokenSeparator, inForce));
    }

    /// <summary>A fresh token: 64 random bits, hex encoded.</summary>
    /// <returns>A token no earlier generation can have used.</returns>
    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>The Redis key holding a prefix's token, inside the configured namespace.</summary>
    /// <param name="prefix">The invalidation boundary.</param>
    /// <returns>The generation key.</returns>
    private RedisKey GenerationKey(string prefix) => _keys.Qualify(string.Concat(KeyspaceSegment, prefix));

    /// <summary>Joins the read already in flight for <paramref name="boundary"/>, or starts one.</summary>
    /// <param name="boundary">The invalidation boundary.</param>
    /// <returns>The token the read resolved to.</returns>
    /// <remarks>
    /// The shared task is published BEFORE the read starts, so a read that completes synchronously
    /// can never remove its entry ahead of the insert and leave a finished task behind.
    /// </remarks>
    private Task<string> ReadShared(string boundary)
    {
        var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = _inFlight.GetOrAdd(boundary, source.Task);

        return ReferenceEquals(shared, source.Task) ? ReadIntoAsync(boundary, source) : shared;
    }

    /// <summary>Runs the read this caller owns, hands its result to every joined reader, then unpublishes it.</summary>
    /// <param name="boundary">The invalidation boundary.</param>
    /// <param name="source">The shared task's source.</param>
    /// <returns>The token the read resolved to.</returns>
    private async Task<string> ReadIntoAsync(string boundary, TaskCompletionSource<string> source)
    {
        try
        {
            var token = await ReadAndRememberAsync(boundary).ConfigureAwait(false);
            source.TrySetResult(token);
            return token;
        }
        catch (OperationCanceledException ex)
        {
            source.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        finally
        {
            // Unpublished only after the token was remembered, so a reader arriving in between finds
            // the fresh entry rather than starting a second round trip.
            _inFlight.TryRemove(new KeyValuePair<string, Task<string>>(boundary, source.Task));
        }
    }

    /// <summary>
    /// Reads a boundary's token from Redis and remembers it; on a failure, falls back to the last
    /// known token (or a fresh one) and remembers that for one local duration as the back-off.
    /// </summary>
    /// <param name="boundary">The invalidation boundary.</param>
    /// <returns>The token in force for this process.</returns>
    private async Task<string> ReadAndRememberAsync(string boundary)
    {
        var readStartedAt = Interlocked.Read(ref _version);

        string token;
        try
        {
            var stored = await _redis!.GetDatabase().StringGetAsync(GenerationKey(boundary)).ConfigureAwait(false);
            token = stored.IsNullOrEmpty ? string.Empty : stored.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            token = _remembered.TryGetValue(boundary, out var lastKnown) ? lastKnown.Token : NewToken();
            LogGenerationReadFailed(_logger, boundary, _localDurationMs, ex);
        }

        return Remember(boundary, token, readStartedAt);
    }

    /// <summary>
    /// Remembers a token for one local duration, unless this process invalidated the prefix after
    /// <paramref name="readStartedAt"/>, in which case that newer token wins. Sweeps entries that
    /// expired more than <see cref="RetentionMultiplier"/> local durations ago now and then.
    /// </summary>
    /// <param name="prefix">The invalidation boundary.</param>
    /// <param name="token">Its token.</param>
    /// <param name="readStartedAt">The version observed when the read began; <see cref="long.MaxValue"/> always stores.</param>
    /// <returns>The token now in force for the boundary.</returns>
    private string Remember(string prefix, string token, long readStartedAt)
    {
        var now = Environment.TickCount64;
        var fromInvalidation = readStartedAt == long.MaxValue;
        var candidate = new RememberedToken(token, now + _localDurationMs, Interlocked.Increment(ref _version), fromInvalidation);

        // A read that began before this process invalidated the prefix must not put the old token
        // back: the invalidating process adopts its own new token at once and keeps it.
        var inForce = _remembered.AddOrUpdate(
            prefix,
            candidate,
            (_, existing) => !fromInvalidation && existing.FromInvalidation && existing.Version > readStartedAt ? existing : candidate);

        if (Interlocked.Increment(ref _insertsSinceSweep) % SweepInterval == 0)
        {
            var retentionMs = _localDurationMs * RetentionMultiplier;
            foreach (var entry in _remembered)
            {
                if (now >= entry.Value.ExpiresAt + retentionMs)
                    _remembered.TryRemove(entry);
            }
        }

        return inForce.Token;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache generation read failed for boundary '{Boundary}'; the last known token (or a fresh one) is used and the read is not retried for {BackOffMilliseconds} ms.")]
    private static partial void LogGenerationReadFailed(ILogger logger, string boundary, long backOffMilliseconds, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prefix cache invalidation is process-local: no IConnectionMultiplexer is registered, so another replica keeps serving an invalidated prefix until its entries expire. Register a Redis client (AddRedisCaching) to invalidate across replicas.")]
    private static partial void LogProcessLocalInvalidation(ILogger logger);

    /// <summary>A remembered token, the tick count it must be re-read after, and its version stamp.</summary>
    /// <param name="Token">The token, empty when the prefix was never invalidated.</param>
    /// <param name="ExpiresAt">The <see cref="Environment.TickCount64"/> value it expires at.</param>
    /// <param name="Version">The monotonic stamp taken when it was stored.</param>
    /// <param name="FromInvalidation">Whether this process stored it by invalidating the prefix.</param>
    private readonly record struct RememberedToken(string Token, long ExpiresAt, long Version, bool FromInvalidation);
}
