using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Settings;
using MMCA.Common.Infrastructure.Caching;

namespace MMCA.Common.Infrastructure.Tests.Settings;

/// <summary>
/// The caching defaults moved from hard-coded constants to a bound <c>Cache</c> section. Two things
/// have to stay true: an unconfigured host keeps exactly the policy <see cref="CacheOptions"/>
/// defines (so nothing changed for anyone who sets nothing), and a configured host actually gets its
/// values through the options pipeline <c>AddCaching</c> wires.
/// </summary>
public sealed class CacheSettingsTests
{
    [Fact]
    public void SectionName_IsCache() =>
        CacheSettings.SectionName.Should().Be("Cache");

    [Fact]
    public void Default_DefaultDuration_MatchesTheHardCodedCachePolicy() =>
        new CacheSettings().DefaultDuration.Should().Be(
            CacheOptions.DefaultDuration,
            "CacheOptions stays the single source of truth for the value, so the two paths cannot drift");

    [Fact]
    public void Default_LocalCacheDuration_IsNull() =>
        new CacheSettings().LocalCacheDuration.Should().BeNull(
            "null keeps the two-level cache's built-in L1 ceiling rather than introducing a second default");

    [Fact]
    public void Default_PopulateLockTimeout_WaitsIndefinitely() =>
        new CacheSettings().PopulateLockTimeout.Should().Be(
            Timeout.InfiniteTimeSpan,
            "the unbounded wait is the stampede protection the populate lock exists for");

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new CacheSettings
        {
            DefaultDuration = TimeSpan.FromMinutes(2),
            LocalCacheDuration = TimeSpan.FromSeconds(5),
            PopulateLockTimeout = TimeSpan.FromSeconds(3),
        };

        sut.DefaultDuration.Should().Be(TimeSpan.FromMinutes(2));
        sut.LocalCacheDuration.Should().Be(TimeSpan.FromSeconds(5));
        sut.PopulateLockTimeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    // ── Binding through AddCaching ──
    [Fact]
    public void AddCaching_WithConfiguration_BindsTheCacheSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Cache:DefaultDuration"] = "00:10:00",
                ["Cache:LocalCacheDuration"] = "00:00:05",
                ["Cache:PopulateLockTimeout"] = "00:00:02",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCaching(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        CacheSettings settings = provider.GetRequiredService<IOptions<CacheSettings>>().Value;

        settings.DefaultDuration.Should().Be(TimeSpan.FromMinutes(10));
        settings.LocalCacheDuration.Should().Be(TimeSpan.FromSeconds(5));
        settings.PopulateLockTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddCaching_WithConfiguration_BindsTheApplicationLayerViewOfTheSameKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Cache:PopulateLockTimeout"] = "00:00:02",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddCaching(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<QueryCachePipelineSettings>>().Value.PopulateLockTimeout
            .Should().Be(
                TimeSpan.FromSeconds(2),
                "the decorator lives in a layer that cannot see CacheSettings, so Infrastructure binds its view of the same key");
    }

    [Fact]
    public void AddCaching_WithoutConfiguration_StillResolvesTheDefaults()
    {
        var services = new ServiceCollection();
        services.AddCaching();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<CacheSettings>>().Value.DefaultDuration
            .Should().Be(CacheOptions.DefaultDuration);
        provider.GetRequiredService<IOptions<QueryCachePipelineSettings>>().Value.PopulateLockTimeout
            .Should().Be(QueryCachePipelineSettings.DefaultPopulateLockTimeout);
    }
}
