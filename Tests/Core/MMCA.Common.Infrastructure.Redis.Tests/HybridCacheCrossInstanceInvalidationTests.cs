using System.Diagnostics;
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
/// The cross-replica property prefix invalidation has to hold: two hosts, each with its own
/// in-process L1, share one Redis. A prefix invalidated on one host must stop being served by the
/// OTHER host's L1 within <c>Cache:LocalCacheDuration</c>, not only by the host that ran the
/// command. A unit test over a fake L2 cannot show this, because the whole question is what a second,
/// independent cache instance learns from the shared server.
/// </summary>
public sealed class HybridCacheCrossInstanceInvalidationTests : IAsyncLifetime
{
    /// <summary>Short on purpose, so the test waits seconds rather than the production 30.</summary>
    private static readonly TimeSpan LocalDuration = TimeSpan.FromSeconds(2);

    /// <summary>Slack on top of <see cref="LocalDuration"/> for container and scheduling latency.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
    private readonly List<ServiceProvider> _providers = [];

    private ConnectionMultiplexer _multiplexer = null!;
    private IDistributedCache _distributedCache = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();

        _multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
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

    [Fact]
    public async Task RemoveByPrefixAsync_OnOneInstance_EndsTheOtherInstancesLocalCopyWithinTheLocalDuration()
    {
        var ct = TestContext.Current.CancellationToken;
        var replicaA = CreateSut();
        var replicaB = CreateSut();
        var prefix = $"MMCA.Sample.Event:{Guid.NewGuid():N}:";
        var key = $"{prefix}list";
        var factoryCalls = 0;

        Task<string> Rebuild(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult("rebuilt");
        }

        await replicaA.SetAsync(key, "original", EntryTtl, ct);

        // B reads it, which puts the value in B's own L1.
        (await replicaB.GetOrCreateAsync(key, Rebuild, EntryTtl, ct)).Should().Be("original");
        factoryCalls.Should().Be(0);

        await replicaA.RemoveByPrefixAsync(prefix, ct);

        // The invalidating replica sees it at once.
        (await replicaA.GetAsync<string>(key, ct)).Should().BeNull();

        // The other replica must miss, and so run the factory, within the local duration.
        var clock = Stopwatch.StartNew();
        string served;
        do
        {
            served = await replicaB.GetOrCreateAsync(key, Rebuild, EntryTtl, ct);
            if (Volatile.Read(ref factoryCalls) > 0)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
        while (clock.Elapsed < LocalDuration + Slack);

        factoryCalls.Should().Be(1, "replica B must stop serving its local copy once the local duration has passed");
        served.Should().Be("rebuilt");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_OnOneInstance_LeavesEntriesOutsideThePrefixOnTheOtherInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var replicaA = CreateSut();
        var replicaB = CreateSut();
        var prefix = $"MMCA.Sample.Event:{Guid.NewGuid():N}:";
        var survivor = $"MMCA.Sample.Session:{Guid.NewGuid():N}:list";

        await replicaA.SetAsync(survivor, "keep", EntryTtl, ct);
        await replicaA.RemoveByPrefixAsync(prefix, ct);

        await Task.Delay(LocalDuration + TimeSpan.FromMilliseconds(500), ct);

        (await replicaB.GetAsync<string>(survivor, ct)).Should().Be("keep");
    }

    /// <summary>
    /// Builds the service as <c>AddCommonHybridCache</c> does, each call with its own
    /// <see cref="HybridCache"/> and therefore its own L1: the shape of two replicas.
    /// </summary>
    /// <returns>A service under test.</returns>
    private HybridCacheService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_distributedCache);
        services.AddHybridCache(options => options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration = EntryTtl,
            LocalCacheExpiration = LocalDuration,
        });

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new HybridCacheService(
            provider.GetRequiredService<HybridCache>(),
            NullLogger<HybridCacheService>.Instance,
            _multiplexer,
            cacheSettings: Options.Create(new CacheSettings { LocalCacheDuration = LocalDuration }));
    }
}
