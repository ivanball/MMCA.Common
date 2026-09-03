using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Caching;

namespace MMCA.Common.Infrastructure.Tests.Caching;

/// <summary>
/// Exercises <see cref="HybridCacheService"/> over a REAL in-process <c>AddHybridCache</c> with a
/// recording <see cref="IDistributedCache"/> standing in for the L2, plus two hand-written
/// <see cref="HybridCache"/> stubs where the assertion is about what this service asks the platform
/// to do (entry options, flags) or about how it behaves when the platform faults.
/// <para>
/// The key shape assertions matter more than they look: the whole design rests on this service
/// writing under a keyspace no other cache implementation can address, so a silent change to the
/// key would reintroduce the mixed-format failure the <c>hc:</c> segment exists to prevent.
/// </para>
/// </summary>
public sealed class HybridCacheServiceTests
{
    private const string Prefix = "svc:";

    // ── Key shape ──
    [Fact]
    public async Task SetAsync_WritesTheL2EntryUnderTheDisjointHybridKeyspace()
    {
        var (sut, l2, _) = CreateSut();

        await sut.SetAsync("catalog:1", "value", cancellationToken: TestContext.Current.CancellationToken);

        l2.Keys.Should().ContainSingle()
            .Which.Should().Be($"{Prefix}hc:catalog:1", "the hc: segment is what keeps the two formats apart");
    }

    [Fact]
    public async Task SetAsync_WithoutAKeyNamespace_StillSegmentsTheKeyspace()
    {
        var l2 = new RecordingDistributedCache();
        var sut = new HybridCacheService(BuildHybridCache(l2), NullLogger<HybridCacheService>.Instance);

        await sut.SetAsync("catalog:1", "value", cancellationToken: TestContext.Current.CancellationToken);

        l2.Keys.Should().ContainSingle().Which.Should().Be("hc:catalog:1");
    }

    // ── Round trip ──
    [Fact]
    public async Task SetGetRemove_RoundTripsThroughBothLevels()
    {
        var (sut, _, _) = CreateSut();

        await sut.SetAsync("k", 42, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        (await sut.GetAsync<int?>("k", TestContext.Current.CancellationToken)).Should().Be(42);

        await sut.RemoveAsync("k", TestContext.Current.CancellationToken);
        (await sut.GetAsync<int?>("k", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_OnAMiss_ReturnsDefaultAndWritesNothing()
    {
        var (sut, l2, _) = CreateSut();

        (await sut.GetAsync<string>("absent", TestContext.Current.CancellationToken)).Should().BeNull();

        l2.Keys.Should().BeEmpty("a read must never populate the cache: DisableUnderlyingData means no factory and no write");
        l2.Writes.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_ReadsAnEntryWrittenBeforeThisProcessCached_ItLocally()
    {
        var l2 = new RecordingDistributedCache();
        var writer = new HybridCacheService(BuildHybridCache(l2), NullLogger<HybridCacheService>.Instance, keyNamespace: new CacheKeyNamespace(Prefix));
        await writer.SetAsync("shared", "written-elsewhere", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // A second process over the same L2: nothing is in its L1, so the value can only come from L2.
        var reader = new HybridCacheService(BuildHybridCache(l2), NullLogger<HybridCacheService>.Instance, keyNamespace: new CacheKeyNamespace(Prefix));

        (await reader.GetAsync<string>("shared", TestContext.Current.CancellationToken)).Should().Be("written-elsewhere");
    }

    // ── Disjoint from the DistributedCacheService keyspace ──
    [Fact]
    public async Task GetAsync_WithADistributedCacheEntryAtTheSameLogicalKey_IsACleanMiss()
    {
        var l2 = new RecordingDistributedCache();
        var other = new DistributedCacheService(l2, NullLogger<DistributedCacheService>.Instance, keyNamespace: new CacheKeyNamespace(Prefix));
        await other.SetAsync("shared", "other-format", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var sut = new HybridCacheService(BuildHybridCache(l2), NullLogger<HybridCacheService>.Instance, keyNamespace: new CacheKeyNamespace(Prefix));

        (await sut.GetAsync<string>("shared", TestContext.Current.CancellationToken)).Should().BeNull(
            "the two keyspaces are disjoint, so the other service's entry is invisible rather than unreadable");
        l2.Keys.Should().ContainSingle().Which.Should().Be($"{Prefix}shared");
    }

    // ── Fail-soft ──
    [Fact]
    public async Task GetAsync_WhenTheCacheFaults_ReturnsDefaultAndDropsTheEntry()
    {
        var faulting = new FaultingHybridCache();
        var sut = new HybridCacheService(faulting, NullLogger<HybridCacheService>.Instance, keyNamespace: new CacheKeyNamespace(Prefix));

        (await sut.GetAsync<string>("poison", TestContext.Current.CancellationToken)).Should().BeNull();

        faulting.Removed.Should().ContainSingle()
            .Which.Should().Be($"{Prefix}hc:poison", "the unreadable entry is dropped so the next write repopulates it");
    }

    [Fact]
    public async Task GetAsync_WhenCancelled_DoesNotSwallowTheCancellation()
    {
        var faulting = new FaultingHybridCache { Fault = new OperationCanceledException() };
        var sut = new HybridCacheService(faulting, NullLogger<HybridCacheService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.GetAsync<string>("k", TestContext.Current.CancellationToken));

        faulting.Removed.Should().BeEmpty("a cancelled read is not a corrupt entry");
    }

    [Fact]
    public async Task GetAsync_WhenTheSelfHealDeleteAlsoFaults_StillAnswersAMiss()
    {
        var faulting = new FaultingHybridCache { FaultTheRemove = true };
        var sut = new HybridCacheService(faulting, NullLogger<HybridCacheService>.Instance);

        (await sut.GetAsync<string>("poison", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ── Entry options ──
    [Fact]
    public async Task SetAsync_WithNoExpiration_AppliesTheFrameworkDefaultToBothLevels()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(recording, NullLogger<HybridCacheService>.Instance);

        await sut.SetAsync("k", "v", cancellationToken: TestContext.Current.CancellationToken);

        recording.LastSetOptions!.Expiration.Should().Be(CacheOptions.DefaultDuration);
        recording.LastSetOptions.LocalCacheExpiration.Should().Be(HybridCacheService.LocalCacheDefault);
    }

    [Fact]
    public async Task SetAsync_WithALongExpiration_ClampsTheLocalCopyToTheLocalDefault()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(recording, NullLogger<HybridCacheService>.Instance);

        await sut.SetAsync("k", "v", TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        recording.LastSetOptions!.Expiration.Should().Be(TimeSpan.FromHours(24));
        recording.LastSetOptions.LocalCacheExpiration.Should().Be(
            HybridCacheService.LocalCacheDefault,
            "a 24h entry must not sit in another replica's memory for 24h after an invalidation it cannot see");
    }

    [Fact]
    public async Task SetAsync_WithAShortExpiration_KeepsTheLocalCopyNoLongerThanTheEntry()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(recording, NullLogger<HybridCacheService>.Instance);

        await sut.SetAsync("k", "v", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        recording.LastSetOptions!.Expiration.Should().Be(TimeSpan.FromSeconds(5));
        recording.LastSetOptions.LocalCacheExpiration.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SetAsync_WithAConfiguredDefaultDuration_UsesItInsteadOfTheHardCodedDefault()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(
            recording,
            NullLogger<HybridCacheService>.Instance,
            cacheSettings: Options.Create(new CacheSettings { DefaultDuration = TimeSpan.FromMinutes(15) }));

        await sut.SetAsync("k", "v", cancellationToken: TestContext.Current.CancellationToken);

        recording.LastSetOptions!.Expiration.Should().Be(
            TimeSpan.FromMinutes(15),
            "the Cache section is what a host configures; CacheOptions only supplies the default it starts from");
        recording.LastSetOptions.LocalCacheExpiration.Should().Be(
            HybridCacheService.LocalCacheDefault,
            "the local copy is still capped at the built-in ceiling when none is configured");
    }

    [Fact]
    public async Task SetAsync_WithAConfiguredLocalCacheDuration_CapsTheLocalCopyAtIt()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(
            recording,
            NullLogger<HybridCacheService>.Instance,
            cacheSettings: Options.Create(new CacheSettings { LocalCacheDuration = TimeSpan.FromSeconds(2) }));

        await sut.SetAsync("k", "v", TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        recording.LastSetOptions!.Expiration.Should().Be(TimeSpan.FromHours(24));
        recording.LastSetOptions.LocalCacheExpiration.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetAsync_ReadsWithTheUnderlyingDataDisabled()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(recording, NullLogger<HybridCacheService>.Instance);

        await sut.GetAsync<string>("k", TestContext.Current.CancellationToken);

        recording.LastGetOptions!.Flags.Should().Be(HybridCacheEntryFlags.DisableUnderlyingData);
        recording.FactoryInvocations.Should().Be(0, "a read must never run a factory");
    }

    // ── IncrementAsync: L2-only counter semantics ──
    [Fact]
    public async Task IncrementAsync_BypassesTheLocalCacheOnBothLegs()
    {
        var recording = new RecordingHybridCache();
        var sut = new HybridCacheService(recording, NullLogger<HybridCacheService>.Instance);

        await sut.IncrementAsync("login:attempts", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        const HybridCacheEntryFlags noLocalCache =
            HybridCacheEntryFlags.DisableLocalCacheRead | HybridCacheEntryFlags.DisableLocalCacheWrite;

        recording.LastGetOptions!.Flags.Should().Be(HybridCacheEntryFlags.DisableUnderlyingData | noLocalCache);
        recording.LastSetOptions!.Flags.Should().Be(noLocalCache);
    }

    [Fact]
    public async Task IncrementAsync_ReadsTheDistributedCopyOnEveryCall()
    {
        var (sut, l2, _) = CreateSut();
        var key = "login:attempts";

        (await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).Should().Be(1);
        (await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).Should().Be(2);
        (await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).Should().Be(3);

        l2.ReadCount($"{Prefix}hc:{key}").Should().Be(3, "a counter served from L1 would be stale and would undercount silently");
    }

    [Fact]
    public async Task IncrementAsync_LeavesNothingInTheLocalCache()
    {
        var (sut, l2, _) = CreateSut();
        var key = "login:attempts";

        await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // Drop the distributed copy: anything still answering now could only come from L1.
        l2.Clear();

        (await sut.GetAsync<long?>(key, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ── GetOrCreateAsync override ──
    [Fact]
    public async Task GetOrCreateAsync_OnAMiss_RunsTheFactoryAndCachesIt()
    {
        var (sut, _, _) = CreateSut();
        var factoryCalls = 0;

        var first = await sut.GetOrCreateAsync(
            "k",
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult("fresh");
            },
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var second = await sut.GetOrCreateAsync(
            "k",
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return Task.FromResult("other");
            },
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        first.Should().Be("fresh");
        second.Should().Be("fresh");
        factoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateAsync_WritesUnderTheHybridKeyspace()
    {
        var (sut, l2, _) = CreateSut();

        await sut.GetOrCreateAsync(
            "catalog:list",
            _ => Task.FromResult("value"),
            cancellationToken: TestContext.Current.CancellationToken);

        // HybridCache returns the factory result to the caller and backfills L2 in the background,
        // so the write is observed rather than assumed to have happened by the time the call returns.
        await WaitForKeyAsync(l2, $"{Prefix}hc:catalog:list", TestContext.Current.CancellationToken);

        l2.Keys.Should().ContainSingle().Which.Should().Be($"{Prefix}hc:catalog:list");
    }

    /// <summary>Waits for a key to appear in the L2, failing the test rather than hanging it.</summary>
    /// <param name="l2">The recording distributed cache.</param>
    /// <param name="key">The raw key expected to appear.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private static async Task WaitForKeyAsync(RecordingDistributedCache l2, string key, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100 && !l2.Keys.Contains(key); attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    [Fact]
    public async Task GetOrCreateAsync_WithoutAFactory_Throws()
    {
        var (sut, _, _) = CreateSut();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sut.GetOrCreateAsync<string>("k", factory: null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── Prefix eviction without a multiplexer ──
    [Fact]
    public async Task RemoveByPrefixAsync_WithoutAMultiplexer_IsANoOpRatherThanAFailure()
    {
        var (sut, l2, _) = CreateSut();
        await sut.SetAsync("catalog:1", "value", cancellationToken: TestContext.Current.CancellationToken);

        await sut.RemoveByPrefixAsync("catalog:", TestContext.Current.CancellationToken);

        l2.Keys.Should().ContainSingle("prefix eviction needs SCAN; without a multiplexer the entry is bounded by its TTL");
    }

    /// <summary>Builds the service over a real HybridCache with the recording L2 behind it.</summary>
    /// <returns>The service under test, its L2, and the provider keeping the cache alive.</returns>
    private static (HybridCacheService Sut, RecordingDistributedCache L2, ServiceProvider Provider) CreateSut()
    {
        var l2 = new RecordingDistributedCache();
        var provider = BuildProvider(l2);

        var sut = new HybridCacheService(
            provider.GetRequiredService<HybridCache>(),
            NullLogger<HybridCacheService>.Instance,
            connectionMultiplexer: null,
            keyNamespace: new CacheKeyNamespace(Prefix));

        return (sut, l2, provider);
    }

    private static HybridCache BuildHybridCache(IDistributedCache l2) =>
        BuildProvider(l2).GetRequiredService<HybridCache>();

    private static ServiceProvider BuildProvider(IDistributedCache l2)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(l2);
        services.AddHybridCache();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// In-memory <see cref="IDistributedCache"/> that records the raw keys, the read count per key
    /// and the number of writes: everything needed to assert the key shape and to prove that a leg
    /// which must bypass L1 really did reach the distributed level.
    /// </summary>
    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
        private int _writes;

        public IReadOnlyCollection<string> Keys => [.. _store.Keys];

        public int Writes => Volatile.Read(ref _writes);

        public int ReadCount(string key) => _reads.TryGetValue(key, out var count) ? count : 0;

        public void Clear() => _store.Clear();

        public byte[]? Get(string key)
        {
            _reads.AddOrUpdate(key, 1, (_, current) => current + 1);
            return _store.TryGetValue(key, out var bytes) ? bytes : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
            Task.FromResult(Get(key));

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key) => _store.TryRemove(key, out _);

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _store[key] = value;
            Interlocked.Increment(ref _writes);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// <see cref="HybridCache"/> stub that records the entry options each call carries. Used where
    /// the assertion is about the request this service makes (expirations, flags) rather than about
    /// the value that comes back.
    /// </summary>
    private sealed class RecordingHybridCache : HybridCache
    {
        private int _factoryInvocations;

        public HybridCacheEntryOptions? LastGetOptions { get; private set; }

        public HybridCacheEntryOptions? LastSetOptions { get; private set; }

        public int FactoryInvocations => Volatile.Read(ref _factoryInvocations);

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            LastGetOptions = options;

            if (options?.Flags?.HasFlag(HybridCacheEntryFlags.DisableUnderlyingData) == true)
                return ValueTask.FromResult<T>(default!);

            Interlocked.Increment(ref _factoryInvocations);
            return factory(state, cancellationToken);
        }

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            LastSetOptions = options;
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    /// <summary>
    /// <see cref="HybridCache"/> stub whose read always faults, so the fail-soft path in
    /// <see cref="HybridCacheService.GetAsync{T}"/> is exercised against a fault this service can
    /// actually see (the platform's own L2 error handling is not what is under test here).
    /// </summary>
    private sealed class FaultingHybridCache : HybridCache
    {
        private readonly ConcurrentQueue<string> _removed = new();

        public Exception Fault { get; init; } = new InvalidOperationException("cache exploded");

        public bool FaultTheRemove { get; init; }

        public IReadOnlyCollection<string> Removed => [.. _removed];

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) =>
            throw Fault;

        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            if (FaultTheRemove)
                throw new InvalidOperationException("delete failed too");

            _removed.Enqueue(key);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
