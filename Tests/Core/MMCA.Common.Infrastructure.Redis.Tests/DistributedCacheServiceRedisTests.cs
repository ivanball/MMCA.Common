using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace MMCA.Common.Infrastructure.Redis.Tests;

/// <summary>
/// Exercises the shipped cache against a REAL Redis.
/// <para>
/// The unit tier mocks <c>IDistributedCache</c>, which means it asserts the calls the cache makes,
/// never the storage format Redis actually ends up holding. That is a blind spot with teeth: Redis
/// keys are typed, <c>INCR</c> creates a <b>string</b>, and the <c>IDistributedCache</c> Redis
/// provider stores every entry as a <b>hash</b> of <c>absexp</c>/<c>sldexp</c>/<c>data</c>. Mixing
/// the two at one key round-trips flawlessly against a mock and answers <c>WRONGTYPE</c> in
/// production, on the ADR-029 rate-limit and lockout counters.
/// </para>
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job.
/// </para>
/// </summary>
public sealed class DistributedCacheServiceRedisTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();

    private ConnectionMultiplexer _multiplexer = null!;
    private IDistributedCache _distributedCache = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();

        var connectionString = _redis.GetConnectionString();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);

        // The same concrete cache Aspire's AddRedisDistributedCache registers in the service hosts.
        _distributedCache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(_multiplexer),
        }));
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
            await _multiplexer.DisposeAsync();

        await _redis.DisposeAsync();
    }

    /// <summary>
    /// Builds the cache exactly as <c>AddCaching</c> does when both a distributed cache and a
    /// multiplexer are registered, which is the production shape in every extracted service host.
    /// </summary>
    private DistributedCacheService CreateSut() =>
        new(
            _distributedCache,
            NullLogger<DistributedCacheService>.Instance,
            _multiplexer);

    // ── The counter round-trip: the exact shape that failed in production ──
    [Fact]
    public async Task IncrementThenRead_RoundTripsThroughRealRedis()
    {
        var sut = CreateSut();
        var key = $"registration:ip:{Guid.NewGuid():N}";

        var first = await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        first.Should().Be(1);

        // The read that used to throw: the second registration from one IP hit this and 500'd,
        // because the key existed in a representation the read side could not address.
        var afterFirst = await sut.GetAsync<long?>(key, TestContext.Current.CancellationToken);
        afterFirst.Should().Be(1, "a counter must be readable through the same cache that wrote it");

        var second = await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        second.Should().Be(2);

        (await sut.GetAsync<long?>(key, TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task IncrementAsync_AppliesATtlSoCountersDoNotLeak()
    {
        var sut = CreateSut();
        var key = $"login:attempts:{Guid.NewGuid():N}";

        await sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var ttl = await _multiplexer.GetDatabase().KeyTimeToLiveAsync(key);
        ttl.Should().NotBeNull("a rate-limit counter without a TTL never resets and locks the subject out forever");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task IncrementAsync_FromMultipleWritersNeverLosesTheKeyEvenIfItLosesCounts()
    {
        // Documents the CURRENT contract honestly. The read-modify-write can undercount under
        // genuine concurrency (which is why the lockout comment in LoginProtectionService says so),
        // but the invariant that must hold is that the key stays readable and monotonic: an
        // unreadable counter takes the endpoint down, an undercount only weakens the limit.
        var sut = CreateSut();
        var key = $"login:attempts:{Guid.NewGuid():N}";

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            sut.IncrementAsync(key, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)));

        var final = await sut.GetAsync<long?>(key, TestContext.Current.CancellationToken);

        final.Should().NotBeNull("concurrent increments must never leave the counter unreadable");
        final.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(20);
    }

    // ── Prefix invalidation over a real SCAN ──
    [Fact]
    public async Task RemoveByPrefixAsync_EvictsMatchingKeysAndLeavesOthers()
    {
        var sut = CreateSut();
        var prefix = $"catalog:{Guid.NewGuid():N}:";

        await sut.SetAsync($"{prefix}a", "one", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await sut.SetAsync($"{prefix}b", "two", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var survivor = $"other:{Guid.NewGuid():N}";
        await sut.SetAsync(survivor, "keep", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        await sut.RemoveByPrefixAsync(prefix, TestContext.Current.CancellationToken);

        (await sut.GetAsync<string>($"{prefix}a", TestContext.Current.CancellationToken)).Should().BeNull();
        (await sut.GetAsync<string>($"{prefix}b", TestContext.Current.CancellationToken)).Should().BeNull();
        (await sut.GetAsync<string>(survivor, TestContext.Current.CancellationToken)).Should().Be("keep");
    }

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
}
