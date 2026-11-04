using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// Two-level cache backed by <see cref="HybridCache"/>: an in-process L1 in front of the registered
/// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> L2, with serialization,
/// L1 promotion and stampede protection supplied by the platform.
/// <para>
/// Registered by <c>AddCommonHybridCache</c>, the one cache a host with a distributed L2 runs. A host
/// without one keeps <see cref="MemoryCacheService"/>.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Every key is written under a disjoint <c>hc:</c> keyspace</b> (<c>{prefix}hc:{key}</c>), so an
/// entry can never meet a value another writer put in the same server (a host's own
/// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> use, or the generation
/// keys under <see cref="CachePrefixGenerations.KeyspaceSegment"/>) in a format it cannot parse.
/// </para>
/// <para>
/// <b>Prefix invalidation never walks the keyspace.</b> Each stored key folds in the generation
/// tokens of the key's <c>:</c> boundaries (<see cref="CachePrefixGenerations"/>), and
/// <see cref="RemoveByPrefixAsync"/> replaces one token. The cost of an invalidation is one Redis
/// write, whatever the size of the keyspace. Another replica stops serving the invalidated prefix
/// once its remembered token expires, within the local expiration (30 seconds by default,
/// <c>Cache:LocalCacheDuration</c> to change it): the same bound its L1 copies carry, and the same
/// order as the delayed re-invalidation the caching command decorator already performs.
/// </para>
/// <para>
/// <b>The exception is a single-use record</b> (an OAuth exchange code, a password-reset or
/// email-confirmation token, the last accepted second-factor time step). Those are read through
/// <see cref="GetFromSharedStoreAsync{T}"/>, which never answers from L1 and never promotes into
/// it, so a record consumed on one replica is a miss on every other replica at once rather than
/// usable a second time for up to the local expiration. Counters get the same treatment: they are
/// incremented through <see cref="IncrementAsync"/>, which reads and writes L2 only, and a caller
/// that reads a counter without incrementing it (the registration rate-limit check in
/// <c>LoginProtectionService</c>) reads it through <see cref="GetFromSharedStoreAsync{T}"/> too, as do
/// the reads whose correctness depends on seeing a removal at once (the login lockout flag and the
/// soft-deleted-user marker). Their keys still carry generation tokens, so invalidating a prefix
/// that covers them converges across replicas within the local expiration like any other entry;
/// nothing in the framework invalidates such a prefix.
/// </para>
/// </remarks>
internal sealed partial class HybridCacheService : ICacheService
{
    /// <summary>
    /// Segment separating this service's entries from anything else in the same server. Applied
    /// inside the configured namespace, so a key reads <c>{prefix}hc:{key}</c>.
    /// </summary>
    internal const string KeyspaceSegment = "hc:";

    /// <summary><see cref="Exception.Data"/> key marking the miss <see cref="TryGetAsync{T}"/> throws to itself.</summary>
    private const string MissMarker = "MMCA.Common.HybridCacheService.Miss";

    /// <summary>
    /// Default in-process (L1) lifetime, and the ceiling applied to every entry: a replica that never
    /// sees an invalidation still re-reads L2 within this window. Matches the default
    /// <c>AddCommonHybridCache</c> configures, and bounds how long a generation token is remembered.
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
    /// A read answered by L2 alone: the read-only flags plus a full L1 bypass (no L1 read, no L1
    /// promotion). Shared by the two reads whose correctness depends on every replica seeing the
    /// same value: the read leg of <see cref="IncrementAsync"/> (a counter) and
    /// <see cref="GetFromSharedStoreAsync{T}"/> (a single-use record, a counter, or a cross-replica
    /// flag such as the lockout flag or the soft-deleted-user marker). See the remarks on each for
    /// why neither may touch L1.
    /// </summary>
    private static readonly HybridCacheEntryOptions SharedStoreReadOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData
            | HybridCacheEntryFlags.DisableLocalCacheRead
            | HybridCacheEntryFlags.DisableLocalCacheWrite,
    };

    /// <summary>
    /// The presence probe behind <see cref="TryGetAsync{T}"/>: the factory runs on a miss (that is
    /// the signal), and the flag exists to keep this read's flags distinct from every other call this
    /// service makes, so <see cref="HybridCache"/> never joins it with a real
    /// <see cref="GetOrCreateAsync{T}"/>. It changes nothing else: the factory never returns, so
    /// there is no value to write to L2 either way.
    /// </summary>
    private static readonly HybridCacheEntryOptions PresenceProbeOptions = new()
    {
        Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite,
    };

    private readonly HybridCache _hybrid;
    private readonly ILogger<HybridCacheService> _logger;

    /// <summary>
    /// Namespace applied to every key so services sharing one cache instance cannot collide.
    /// Defaults to no prefix, which is correct for a host that owns its cache outright.
    /// </summary>
    private readonly CacheKeyNamespace _keys;

    /// <summary>
    /// Bound <c>Cache</c> section, or the framework defaults when a host builds this service without
    /// one (direct construction in tests). The defaults reproduce
    /// <see cref="CacheOptions.DefaultDuration"/> and <see cref="LocalCacheDefault"/> exactly.
    /// </summary>
    private readonly CacheSettings _settings;

    /// <summary>The generation tokens folded into every stored key; see <see cref="CachePrefixGenerations"/>.</summary>
    private readonly CachePrefixGenerations _generations;

    /// <summary>Initializes a new instance of the <see cref="HybridCacheService"/> class.</summary>
    /// <param name="hybrid">The two-level cache.</param>
    /// <param name="logger">Receives the fail-soft warnings.</param>
    /// <param name="connectionMultiplexer">
    /// The shared Redis server the generation tokens live on, so a prefix invalidation reaches every
    /// replica; <see langword="null"/> keeps the tokens in this process.
    /// </param>
    /// <param name="keyNamespace">Namespace applied to every key and generation key.</param>
    /// <param name="cacheSettings">The bound <c>Cache</c> section.</param>
    public HybridCacheService(
        HybridCache hybrid,
        ILogger<HybridCacheService> logger,
        IConnectionMultiplexer? connectionMultiplexer = null,
        CacheKeyNamespace? keyNamespace = null,
        IOptions<CacheSettings>? cacheSettings = null)
    {
        ArgumentNullException.ThrowIfNull(hybrid);
        ArgumentNullException.ThrowIfNull(logger);

        _hybrid = hybrid;
        _logger = logger;
        _keys = keyNamespace ?? CacheKeyNamespace.None;
        _settings = cacheSettings?.Value ?? new CacheSettings();
        _generations = new CachePrefixGenerations(
            connectionMultiplexer,
            _keys,
            _settings.LocalCacheDuration ?? LocalCacheDefault,
            logger);
    }

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
        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);

        try
        {
            return await _hybrid.GetOrCreateAsync(
                fullKey,
                static _ => ValueTask.FromResult<T?>(default),
                ReadOnlyOptions,
                tags: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailed(_logger, key, ex);
            await SelfHealAsync(fullKey, cancellationToken).ConfigureAwait(false);
            return default;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Overrides the interface default, which infers presence from a non-null
    /// <see cref="GetAsync{T}"/> result and so reports a value-type miss (<c>default(T)</c>, which is
    /// not null) as found. <see cref="HybridCache"/> has no presence-reporting read, so presence is
    /// read from whether it had to call the factory: a hit (L1, or L2 promoted into L1 exactly as
    /// <see cref="GetAsync{T}"/> promotes) never calls it, and on a miss it throws a
    /// <see cref="KeyNotFoundException"/> tagged with <see cref="MissMarker"/>, caught here as "not
    /// found". A thrown sentinel, rather than a flag the factory sets, because a concurrent read of
    /// the same key joins the first caller's in-flight lookup and never runs its own factory; the
    /// exception reaches every joined caller, and nothing is written for it.
    /// </para>
    /// <para>
    /// The read carries <see cref="PresenceProbeOptions"/>, whose flags no other call here uses:
    /// <see cref="HybridCache"/> only joins callers with identical flags, so a
    /// <see cref="GetOrCreateAsync{T}"/> in flight on the same key can never be handed that miss.
    /// Faults are fail-soft exactly like <see cref="GetAsync{T}"/>.
    /// </para>
    /// </remarks>
    public async Task<(bool Found, T? Value)> TryGetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);

        try
        {
            var value = await _hybrid.GetOrCreateAsync<T?>(
                fullKey,
                static _ => throw CacheMiss(),
                PresenceProbeOptions,
                tags: null,
                cancellationToken).ConfigureAwait(false);

            return (true, value);
        }
        catch (KeyNotFoundException ex) when (ex.Data.Contains(MissMarker))
        {
            return (false, default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailed(_logger, key, ex);
            await SelfHealAsync(fullKey, cancellationToken).ConfigureAwait(false);
            return (false, default);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Overrides the interface default (which calls <see cref="GetAsync{T}"/>) because this service
    /// has a process-local tier: the read disables both the L1 read and the L1 write, so the answer
    /// always comes from L2 and nothing is promoted into this replica's memory. A removal on another
    /// replica is therefore seen immediately, which is what keeps a single-use record single-use.
    /// </para>
    /// <para>
    /// Fail-soft exactly like <see cref="GetAsync{T}"/>: a fault is logged at warning level,
    /// answered as a miss (for a single-use record, a refusal, which is the safe direction) and the
    /// offending entry is dropped best-effort. <see cref="OperationCanceledException"/> still
    /// surfaces as cancellation.
    /// </para>
    /// </remarks>
    public async Task<T?> GetFromSharedStoreAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);

        try
        {
            return await _hybrid.GetOrCreateAsync(
                fullKey,
                static _ => ValueTask.FromResult<T?>(default),
                SharedStoreReadOptions,
                tags: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReadFailed(_logger, key, ex);
            await SelfHealAsync(fullKey, cancellationToken).ConfigureAwait(false);
            return default;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The absolute expiration is the caller's, or <see cref="CacheSettings.DefaultDuration"/> when
    /// omitted. The L1 copy is capped at the shorter of that TTL and
    /// <see cref="CacheSettings.LocalCacheDuration"/> (<see cref="LocalCacheDefault"/> when unset),
    /// so a long-lived entry does not sit in another replica's memory for its whole TTL after an
    /// invalidation this process cannot see.
    /// </remarks>
    public async Task SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);
        await _hybrid.SetAsync(fullKey, value, WriteOptions(expiration), tags: null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removes both levels for this process: <see cref="HybridCache"/> clears this process's L1 copy
    /// along with the L2 entry. Other replicas' L1 copies are not reached and keep serving the removed
    /// value until they expire, up to <c>Cache:LocalCacheDuration</c> (30 seconds by default); a read
    /// that must see the removal everywhere goes through <see cref="GetFromSharedStoreAsync{T}"/>.
    /// </remarks>
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);
        await _hybrid.RemoveAsync(fullKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Replaces the generation token of <paramref name="prefix"/> (one Redis write), so every entry
    /// stored under a key that starts with it stops being addressed, here at once and on every other
    /// replica within <c>Cache:LocalCacheDuration</c>. Nothing is scanned or deleted: the orphaned
    /// entries expire on their own TTL. See <see cref="CachePrefixGenerations"/> for why this is a
    /// generation token rather than <see cref="HybridCache.RemoveByTagAsync(string, CancellationToken)"/>.
    /// </para>
    /// <para>
    /// Without an <see cref="IConnectionMultiplexer"/> the token lives in this process, so the
    /// invalidation reaches this process only; that is warned once.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is non-empty and does not end with <c>:</c>.</exception>
    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        CachePrefixContract.EnsureBoundary(prefix);
        return _generations.InvalidateAsync(prefix, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// A read-modify-write with the honest limitation the interface documents: concurrent increments
    /// can undercount. It is not routed through <see cref="GetAsync{T}"/> and <see cref="SetAsync{T}"/>
    /// because both legs must bypass L1 (<see cref="HybridCacheEntryFlags.DisableLocalCacheRead"/> and
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
        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);

        var current = await _hybrid.GetOrCreateAsync(
            fullKey,
            static _ => ValueTask.FromResult<long?>(null),
            SharedStoreReadOptions,
            tags: null,
            cancellationToken).ConfigureAwait(false) ?? 0;

        var next = current + 1;

        await _hybrid.SetAsync(
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
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? expiration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var fullKey = await HybridKeyAsync(key, cancellationToken).ConfigureAwait(false);

        // The factory is passed as state rather than captured, so the delegate stays static and no
        // closure is allocated per call.
        return await _hybrid.GetOrCreateAsync(
            fullKey,
            factory,
            static async (state, ct) => await state(ct).ConfigureAwait(false),
            WriteOptions(expiration),
            tags: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The miss <see cref="TryGetAsync{T}"/>'s factory throws, tagged so it cannot be confused with a
    /// <see cref="KeyNotFoundException"/> raised anywhere else. Never escapes this class.
    /// </summary>
    /// <returns>A tagged <see cref="KeyNotFoundException"/>.</returns>
    private static KeyNotFoundException CacheMiss()
    {
        var miss = new KeyNotFoundException("Cache miss reported by the presence probe.");
        miss.Data[MissMarker] = true;
        return miss;
    }

    /// <summary>
    /// Qualifies a caller-supplied key into this service's disjoint keyspace, with the generation
    /// tokens of its boundaries folded in.
    /// </summary>
    /// <param name="key">The caller-supplied key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully qualified key, <c>{prefix}hc:{key}</c> plus any generation suffix.</returns>
    private async ValueTask<string> HybridKeyAsync(string key, CancellationToken cancellationToken)
    {
        var suffix = await _generations.SuffixAsync(key, cancellationToken).ConfigureAwait(false);
        return _keys.Qualify(string.Concat(KeyspaceSegment, key, suffix));
    }

    /// <summary>
    /// Builds the entry options for a write: the caller's TTL (or the configured default), with the
    /// L1 copy capped at the configured local ceiling or the TTL, whichever is shorter.
    /// </summary>
    /// <param name="expiration">The caller-supplied expiration, if any.</param>
    /// <param name="flags">Optional flags; <see langword="null"/> inherits the configured defaults.</param>
    /// <returns>The entry options to pass to <see cref="HybridCache"/>.</returns>
    private HybridCacheEntryOptions WriteOptions(TimeSpan? expiration, HybridCacheEntryFlags? flags = null)
    {
        var ttl = expiration ?? _settings.DefaultDuration;
        var localCeiling = _settings.LocalCacheDuration ?? LocalCacheDefault;

        return new HybridCacheEntryOptions
        {
            Expiration = ttl,
            LocalCacheExpiration = ttl < localCeiling ? ttl : localCeiling,
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
            await _hybrid.RemoveAsync(fullKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSelfHealFailed(_logger, fullKey, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Two-level cache read failed for key '{CacheKey}'; the read is treated as a miss and the entry is dropped so the next write repopulates it.")]
    private static partial void LogReadFailed(ILogger logger, string cacheKey, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Two-level cache could not drop the unreadable entry '{CacheKey}'; it stays unreadable until its TTL expires.")]
    private static partial void LogSelfHealFailed(ILogger logger, string cacheKey, Exception exception);
}
