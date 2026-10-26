using AwesomeAssertions;
using Microsoft.AspNetCore.OutputCaching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Caching;

/// <summary>
/// SEC-Common-53 / ADR-135: the Redis output-cache store must be namespaced per application like the
/// cache and lock keys, so two applications sharing one Redis never serve or evict each other's
/// output-cache entries.
/// </summary>
public sealed class OutputCacheNamespaceTests
{
    private static ServiceProvider BuildProvider(
        Action<RedisOutputCacheOptions>? hostConfigure = null,
        params (string Key, string? Value)[] entries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ApplicationName).Returns("MMCA.Demo.Catalog");

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(environment.Object);
        if (hostConfigure is not null)
        {
            services.Configure(hostConfigure);
        }

        services.AddCaching(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void InstanceName_DefaultsToTheApplicationNamespace()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<RedisOutputCacheOptions>>().Value;

        options.InstanceName.Should().Be("MMCA-Demo-Catalog:");
    }

    [Fact]
    public void InstanceName_FollowsAnExplicitCacheKeyPrefix()
    {
        using var provider = BuildProvider(entries: ("Cache:KeyPrefix", "conference:"));

        var options = provider.GetRequiredService<IOptions<RedisOutputCacheOptions>>().Value;

        options.InstanceName.Should().Be("conference:");
    }

    [Fact]
    public void InstanceName_KeepsAValueTheHostSetExplicitly()
    {
        using var provider = BuildProvider(o => o.InstanceName = "pinned:");

        var options = provider.GetRequiredService<IOptions<RedisOutputCacheOptions>>().Value;

        options.InstanceName.Should().Be("pinned:");
    }
}
