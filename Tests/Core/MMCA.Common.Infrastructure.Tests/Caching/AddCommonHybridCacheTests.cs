using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.Caching;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Caching;

/// <summary>
/// Covers the opt-in <c>AddCommonHybridCache</c> registration. The load-bearing property is order
/// independence: a host calls it wherever its composition root happens to put it, before or after
/// <c>AddInfrastructure</c>, and the two-level cache has to win either way. It is also a negative
/// contract: a host that does NOT call it must be left exactly as it is today.
/// </summary>
public sealed class AddCommonHybridCacheTests
{
    [Fact]
    public void AddCommonHybridCache_CalledBeforeAddCaching_StillWins()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IDistributedCache>().Object);

        services.AddCommonHybridCache();
        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheService>().Should().BeOfType<HybridCacheService>(
            "AddCaching registers with TryAdd, so an existing registration must survive it");
    }

    [Fact]
    public void AddCommonHybridCache_CalledAfterAddCaching_ReplacesTheRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IDistributedCache>().Object);

        services.AddCaching();
        services.AddCommonHybridCache();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheService>().Should().BeOfType<HybridCacheService>();
    }

    [Fact]
    public void AddCommonHybridCache_LeavesExactlyOneCacheServiceRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.AddCommonHybridCache();

        services.Count(d => d.ServiceType == typeof(ICacheService)).Should().Be(1);
    }

    [Fact]
    public void AddCommonHybridCache_ReplacesAHostsOwnCacheService()
    {
        // Documented behavior, not an accident: calling this is a statement that the two-level cache
        // is the cache for this host.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<ICacheService>().Object);

        services.AddCommonHybridCache();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheService>().Should().BeOfType<HybridCacheService>();
    }

    // ── The untouched default path ──
    [Fact]
    public void WithoutAddCommonHybridCache_ADistributedHostKeepsTheDistributedCacheService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IDistributedCache>().Object);
        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheService>().Should().BeOfType<DistributedCacheService>();
    }

    [Fact]
    public void WithoutAddCommonHybridCache_AMemoryOnlyHostKeepsTheMemoryCacheService()
    {
        var services = new ServiceCollection();
        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICacheService>().Should().BeOfType<MemoryCacheService>();
    }

    // ── Options ──
    [Fact]
    public void AddCommonHybridCache_AppliesTheFrameworkEntryDefaults()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommonHybridCache();

        using ServiceProvider provider = services.BuildServiceProvider();
        HybridCacheOptions options = provider.GetRequiredService<IOptions<HybridCacheOptions>>().Value;

        options.DefaultEntryOptions!.Expiration.Should().Be(CacheOptions.DefaultDuration);
        options.DefaultEntryOptions.LocalCacheExpiration.Should().Be(HybridCacheService.LocalCacheDefault);
    }

    [Fact]
    public void AddCommonHybridCache_LetsTheHostOverrideTheDefaults()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommonHybridCache(options =>
        {
            options.MaximumPayloadBytes = 4096;
            options.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) };
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        HybridCacheOptions options = provider.GetRequiredService<IOptions<HybridCacheOptions>>().Value;

        options.MaximumPayloadBytes.Should().Be(4096);
        options.DefaultEntryOptions!.Expiration.Should().Be(TimeSpan.FromMinutes(10),
            "the host hook runs after the framework defaults, so it has the last word");
    }

    [Fact]
    public void AddCommonHybridCache_RegistersTheHybridCacheItself()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommonHybridCache();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<HybridCache>().Should().NotBeNull();
    }
}
