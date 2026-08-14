using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace MMCA.Common.Infrastructure.Redis.Tests;

/// <summary>
/// Exercises the two-level cache against a REAL Redis, which is the only place the properties that
/// matter here are observable.
/// <para>
/// The unit tier proves what this service ASKS for (key shape, entry options, flags) against a fake
/// L2. What it structurally cannot prove is what a server ends up holding, and that is exactly the
/// class of bug this design exists to prevent: <c>HybridCache</c> and <c>IDistributedCache</c> write
/// different payload formats, so a shared key would answer a read with a value the reader cannot
/// parse. Here the two formats genuinely coexist in one server and the assertion is that they never
/// meet: an entry from the other format is a clean MISS, never an exception.
/// </para>
/// <para>
/// It also covers prefix eviction end to end. <c>RemoveByPrefixAsync</c> runs a real SCAN over two
/// patterns, and only a real server distinguishes "evicted both keyspaces" from "evicted the one the
/// test happened to write".
/// </para>
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job.
/// </para>
/// </summary>
public sealed class HybridCacheServiceRedisTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private readonly List<ServiceProvider> _providers = [];

    private ConnectionMultiplexer _multiplexer = null!;
    private IDistributedCache _distributedCache = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();

        var connectionString = _redis.GetConnectionString();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);

        // The same concrete cache Aspire's AddRedisDistributedCache registers in the service hosts,
        // and the L2 HybridCache is layered over.
        _distributedCache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(_multiplexer),
        }));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        if (_multiplexer is not null)
            await _multiplexer.DisposeAsync();

        await _redis.DisposeAsync();
    }

    // ── Round trip and raw key shape ──
    [Fact]
    public async Task SetGetRemove_RoundTripsThroughRealRedis()
    {
        var sut = CreateSut();
        var key = $"smoke:{Guid.NewGuid():N}";

        await sut.SetAsync(key, 42, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        (await sut.GetAsync<int?>(key, TestContext.Current.CancellationToken)).Should().Be(42);

        await sut.RemoveAsync(key, TestContext.Current.CancellationToken);
        (await sut.GetAsync<int?>(key, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_LandsOnTheServerUnderTheHybridKeyspaceOnly()
    {
        var sut = CreateSut();
        var key = $"catalog:{Guid.NewGuid():N}";
        var db = _multiplexer.GetDatabase();

        await sut.SetAsync(key, "value", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        (await db.KeyExistsAsync($"hc:{key}")).Should().BeTrue("the server must hold the entry under the segmented key");
        (await db.KeyExistsAsync(key)).Should().BeFalse("nothing may be written under the legacy shape");
    }

    // ── The coexistence property the whole design rests on ──
    [Fact]
    public async Task AnEntryWrittenByTheOldCache_IsASoftMissHereNeverAFault()
    {
        var key = $"coexist:{Guid.NewGuid():N}";
        var legacy = CreateLegacy();
        var sut = CreateSut();

        await legacy.SetAsync(key, "old-format", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // This is the read that WOULD have thrown if both formats shared one key.
        var read = await sut.GetAsync<string>(key, TestContext.Current.CancellationToken);

        read.Should().BeNull("the old entry lives in a keyspace this service does not address");
        (await legacy.GetAsync<string>(key, TestContext.Current.CancellationToken)).Should().Be(
            "old-format",
            "and the old cache keeps reading its own entry, which is what makes a rolling deploy safe");
    }

    [Fact]
    public async Task AnEntryWrittenHere_IsASoftMissForTheOldCache()
    {
        var key = $"coexist:{Guid.NewGuid():N}";
        var legacy = CreateLegacy();
        var sut = CreateSut();

        await sut.SetAsync(key, "new-format", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        (await legacy.GetAsync<string>(key, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ── Dual-pattern prefix eviction ──
    [Fact]
    public async Task RemoveByPrefixAsync_EvictsBothKeyspacesAndLeavesEverythingElse()
    {
        var prefix = $"catalog:{Guid.NewGuid():N}:";
        var sut = CreateSut();
        var legacy = CreateLegacy();

        await sut.SetAsync($"{prefix}a", "one", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await sut.SetAsync($"{prefix}b", "two", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // Written by the PREVIOUS implementation under the same logical prefix: a 24h idempotency
        // record outlives the deploy that switched the host over, and must still be evictable.
        await legacy.SetAsync($"{prefix}legacy", "old", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var survivor = $"other:{Guid.NewGuid():N}";
        await sut.SetAsync(survivor, "keep", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await legacy.SetAsync(survivor, "keep-old", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        await sut.RemoveByPrefixAsync(prefix, TestContext.Current.CancellationToken);

        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().BeNull();
        (await sut.GetAsync<string>($"{prefix}b", TestContext.Current.CancellationToken)).Should().BeNull();
        (await legacy.GetAsync<string>($"{prefix}legacy", TestContext.Current.CancellationToken)).Should().BeNull(
            "the legacy pattern is the second half of the eviction, not an afterthought");

        (await sut.GetAsync<string>(survivor, TestContext.Current.CancellationToken)).Should().Be("keep");
        (await legacy.GetAsync<string>(survivor, TestContext.Current.CancellationToken)).Should().Be("keep-old");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_AlsoDropsTheEvictingProcessLocalCopy()
    {
        var prefix = $"catalog:{Guid.NewGuid():N}:";
        var sut = CreateSut();

        await sut.SetAsync($"{prefix}a", "one", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // Read it back first so the value is definitely resident in this instance's L1: a raw key
        // delete would clear the server and leave this read serving the stale local copy.
        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().Be("one");

        await sut.RemoveByPrefixAsync(prefix, TestContext.Current.CancellationToken);

        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    // ── Counters across replicas ──
    [Fact]
    public async Task IncrementAsync_AcrossTwoInstancesSharingOneRedis_StaysMonotonic()
    {
        // Two services with independent in-process caches over one server: the shape of two replicas
        // behind a load balancer. A counter served from either L1 would repeat a value here.
        var replicaA = CreateSut();
        var replicaB = CreateSut();
        var key = $"login:attempts:{Guid.NewGuid():N}";
        var ttl = TimeSpan.FromMinutes(5);

        (await replicaA.IncrementAsync(key, ttl, TestContext.Current.CancellationToken)).Should().Be(1);
        (await replicaB.IncrementAsync(key, ttl, TestContext.Current.CancellationToken)).Should().Be(2);
        (await replicaA.IncrementAsync(key, ttl, TestContext.Current.CancellationToken)).Should().Be(3);
        (await replicaB.IncrementAsync(key, ttl, TestContext.Current.CancellationToken)).Should().Be(4);
    }

    [Fact]
    public async Task IncrementAsync_AppliesATtlSoCountersDoNotLeak()
    {
        var sut = CreateSut();
        var key = $"login:attempts:{Guid.NewGuid():N}";

        await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var ttl = await _multiplexer.GetDatabase().KeyTimeToLiveAsync($"hc:{key}");
        ttl.Should().NotBeNull("a rate-limit counter without a TTL never resets and locks the subject out forever");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    // ── GetOrCreateAsync over a real server ──
    [Fact]
    public async Task GetOrCreateAsync_RunsTheFactoryOnceAndServesTheStoredValueAfterwards()
    {
        var sut = CreateSut();
        var key = $"factory:{Guid.NewGuid():N}";
        var calls = 0;

        var first = await sut.GetOrCreateAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("built");
            },
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        var second = await sut.GetOrCreateAsync(
            key,
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult("rebuilt");
            },
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        first.Should().Be("built");
        second.Should().Be("built");
        calls.Should().Be(1);
    }

    /// <summary>
    /// Builds the service exactly as <c>AddCommonHybridCache</c> does: its own two-level cache over
    /// the shared Redis, with the multiplexer supplied so prefix eviction can SCAN. Each call gets an
    /// independent L1, which is what makes the cross-replica assertions meaningful.
    /// </summary>
    /// <returns>A service under test.</returns>
    private HybridCacheService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_distributedCache);
        services.AddHybridCache();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new HybridCacheService(
            provider.GetRequiredService<HybridCache>(),
            NullLogger<HybridCacheService>.Instance,
            _multiplexer);
    }

    /// <summary>The previous implementation, still writing its own format into the same server.</summary>
    /// <returns>A cache in the legacy keyspace.</returns>
    private DistributedCacheService CreateLegacy() =>
        new(_distributedCache, NullLogger<DistributedCacheService>.Instance, _multiplexer);
}
