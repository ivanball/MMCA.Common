using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
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
/// class of bug this design exists to prevent: <c>HybridCache</c> and a plain <c>IDistributedCache</c>
/// writer use different payload formats, so a shared key would answer a read with a value the reader
/// cannot parse. Here the two formats genuinely coexist in one server and the assertion is that they
/// never meet: an entry from the other format is a clean MISS, never an exception.
/// </para>
/// <para>
/// It also covers prefix invalidation end to end: <c>RemoveByPrefixAsync</c> writes one generation
/// token to the server, and only a real server shows that every entry under the prefix stops being
/// addressed while nothing is scanned. The cross-replica half lives in
/// <see cref="HybridCacheCrossInstanceInvalidationTests"/>.
/// </para>
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job.
/// </para>
/// </summary>
public sealed class HybridCacheServiceRedisTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
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
        (await db.KeyExistsAsync(key)).Should().BeFalse("nothing may be written under the bare key another IDistributedCache writer would use");
    }

    // ── The coexistence property the whole design rests on ──
    [Fact]
    public async Task AnEntryWrittenByAnotherWriterAtTheBareKey_IsASoftMissHereNeverAFault()
    {
        // Another IDistributedCache writer in the same server (a host's own use of it) writes its
        // own payload format at the bare key. This is the read that WOULD have thrown if both
        // formats shared one key.
        var key = $"coexist:{Guid.NewGuid():N}";
        var sut = CreateSut();

        await _distributedCache.SetStringAsync(key, "other-format", TestContext.Current.CancellationToken);

        (await sut.GetAsync<string>(key, TestContext.Current.CancellationToken)).Should().BeNull(
            "the other writer's entry lives in a keyspace this service does not address");
        (await _distributedCache.GetStringAsync(key, TestContext.Current.CancellationToken)).Should().Be("other-format");
    }

    // ── Prefix eviction ──
    [Fact]
    public async Task RemoveByPrefixAsync_EvictsItsOwnKeyspaceAndLeavesEverythingElse()
    {
        var prefix = $"catalog:{Guid.NewGuid():N}:";
        var sut = CreateSut();

        await sut.SetAsync($"{prefix}a", "one", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await sut.SetAsync($"{prefix}b", "two", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var survivor = $"other:{Guid.NewGuid():N}";
        await sut.SetAsync(survivor, "keep", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await _distributedCache.SetStringAsync(survivor, "keep-other", TestContext.Current.CancellationToken);

        await sut.RemoveByPrefixAsync(prefix, TestContext.Current.CancellationToken);

        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().BeNull();
        (await sut.GetAsync<string>($"{prefix}b", TestContext.Current.CancellationToken)).Should().BeNull();
        (await sut.GetAsync<string>(survivor, TestContext.Current.CancellationToken)).Should().Be("keep");
        (await _distributedCache.GetStringAsync(survivor, TestContext.Current.CancellationToken)).Should().Be("keep-other");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WritesOneGenerationKeyAndScansNothing()
    {
        // The point of the generation design: an invalidation is one write to one known key, so its
        // cost does not grow with the keyspace. The entry itself is left to expire on its TTL.
        var prefix = $"catalog:{Guid.NewGuid():N}:";
        var sut = CreateSut();
        var db = _multiplexer.GetDatabase();

        await sut.SetAsync($"{prefix}a", "one", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await sut.RemoveByPrefixAsync(prefix, TestContext.Current.CancellationToken);

        (await db.KeyExistsAsync($"hcg:{prefix}")).Should().BeTrue("the invalidation is recorded as the prefix's generation token");
        (await db.KeyExistsAsync($"hc:{prefix}a")).Should().BeTrue("nothing is deleted: the orphaned entry expires on its own TTL");
        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().BeNull();

        await sut.SetAsync($"{prefix}a", "two", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().Be("two", "a write after the invalidation is addressed under the new generation");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_RejectsAPrefixThatStopsMidSegment()
    {
        var sut = CreateSut();

        var act = () => sut.RemoveByPrefixAsync("catalog", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
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
    public async Task GetFromSharedStoreAsync_AfterAnotherInstanceRemovedTheKey_IsAMissDespiteTheLocalCopy()
    {
        // Single-use records (exchange codes, reset tokens): replica B holds the record in its L1,
        // replica A consumes and removes it, and B's single-use read must see the removal.
        var replicaA = CreateSut();
        var replicaB = CreateSut();
        var key = $"oauth-exchange:{Guid.NewGuid():N}";

        await replicaA.SetAsync(key, "token-pair", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        (await replicaB.GetAsync<string>(key, TestContext.Current.CancellationToken)).Should().Be("token-pair");

        await replicaA.RemoveAsync(key, TestContext.Current.CancellationToken);

        var read = await ((ICacheService)replicaB).GetFromSharedStoreAsync<string>(key, TestContext.Current.CancellationToken);
        read.Should().BeNull("the record was consumed on another replica; only B's local copy still holds it");
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
    /// the shared Redis, with the multiplexer supplied so prefix invalidation reaches the server. Each call gets an
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

}
