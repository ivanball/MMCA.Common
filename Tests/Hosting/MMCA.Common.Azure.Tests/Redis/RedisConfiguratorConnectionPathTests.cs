using AwesomeAssertions;
using Microsoft.AspNetCore.OutputCaching.StackExchangeRedis;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Caching;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;

namespace MMCA.Common.Azure.Tests.Redis;

/// <summary>
/// Security lane D1, the one Redis extension point across every framework connection: the Redis
/// client and the distributed cache over it (<c>AddRedisCaching</c>), the output-cache store
/// (<c>AddRedisOutputCaching</c>), the SignalR backplane (<c>AddPushNotifications</c>) and the
/// <c>redis</c> PING health check's own connection (<c>AddInfrastructureHealthChecks</c>). This test
/// project is the one that sees both public sides (MMCA.Common.Infrastructure and MMCA.Common.Aspire).
/// Only the built options are read; the health-check case uses a configurator that throws, so the
/// check never reaches its connect.
/// </summary>
public sealed class RedisConfiguratorConnectionPathTests
{
    private const string RedisConnectionString = "cache.example.invalid:6380,ssl=true,abortConnect=false,connectTimeout=1000";

    private static HostApplicationBuilder Builder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:redis"] = RedisConnectionString,
            ["PushNotifications:Enabled"] = "true",
            ["PushNotifications:HubPath"] = "/hubs/test",
        });
        return builder;
    }

    private static ServiceProvider AllOptionPaths(IRedisConfigurationOptionsConfigurator? configurator)
    {
        var builder = Builder();
        if (configurator is not null)
        {
            builder.Services.AddRedisConfigurationOptionsConfigurator(configurator);
        }

        builder.AddRedisCaching();
        builder.AddRedisOutputCaching();
        builder.Services.AddPushNotifications(builder.Configuration);
        return builder.Services.BuildServiceProvider();
    }

    private static string Describe(ConfigurationOptions options) => options.ToString(includePassword: true);

    [Fact]
    public void NoConfigurator_RedisClient_HasExactlyTheConnectionStringOptions()
    {
        using var provider = AllOptionPaths(configurator: null);

        var client = provider.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        Describe(client).Should().Be(Describe(ConfigurationOptions.Parse(RedisConnectionString)));
    }

    [Fact]
    public void NoConfigurator_DistributedCache_UsesTheClientConnectionAndNoOptionsOfItsOwn()
    {
        using var provider = AllOptionPaths(configurator: null);

        var cache = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;

        cache.ConnectionMultiplexerFactory.Should().NotBeNull("the distributed cache rides the Redis client connection");
        cache.ConfigurationOptions.Should().BeNull();
        cache.Configuration.Should().BeNullOrEmpty();
    }

    [Fact]
    public void NoConfigurator_OutputCache_ConnectsFromTheConnectionStringAlone()
    {
        using var provider = AllOptionPaths(configurator: null);

        var outputCache = provider.GetRequiredService<IOptions<RedisOutputCacheOptions>>().Value;

        outputCache.Configuration.Should().Be(RedisConnectionString);
        outputCache.ConfigurationOptions.Should().BeNull("with no configurator the store is not moved onto parsed options");
    }

    [Fact]
    public void NoConfigurator_SignalRBackplane_HasExactlyTheConnectionStringOptions()
    {
        using var provider = AllOptionPaths(configurator: null);

        var backplane = provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;

        var expected = ConfigurationOptions.Parse(RedisConnectionString);
        expected.ChannelPrefix = backplane.ChannelPrefix;
        Describe(backplane).Should().Be(Describe(expected));
    }

    [Fact]
    public void WithAConfigurator_TheSameConfiguratorRunsOncePerConnectionPath_OnThatPathsOptions()
    {
        var configurator = new RecordingConfigurator();
        using var provider = AllOptionPaths(configurator);

        var client = provider.GetRequiredService<IOptions<ConfigurationOptions>>().Value;
        var outputCache = provider.GetRequiredService<IOptions<RedisOutputCacheOptions>>().Value.ConfigurationOptions;
        var backplane = provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;

        // Resolving each path again does not run the configurator again.
        _ = provider.GetRequiredService<IOptions<ConfigurationOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<RedisOutputCacheOptions>>().Value;
        _ = provider.GetRequiredService<IOptions<RedisOptions>>().Value;

        outputCache.Should().NotBeNull("a registered configurator moves the output-cache store onto configured options");
        configurator.Calls.Should().HaveCount(3, "one run per connection path: client, output cache, backplane");
        configurator.Calls.Should().Contain(call => ReferenceEquals(call, client));
        configurator.Calls.Should().Contain(call => ReferenceEquals(call, outputCache));
        configurator.Calls.Should().Contain(call => ReferenceEquals(call, backplane));

        foreach (var options in new[] { client, outputCache!, backplane })
        {
            options.ClientName.Should().Be(RecordingConfigurator.Mark, "the configured options are the ones the path connects with");
            options.EndPoints.Select(EndPointCollection.ToString).Should().Equal("cache.example.invalid:6380");
            options.Ssl.Should().BeTrue();
        }
    }

    [Fact]
    public void WithAConfigurator_DistributedCache_StillRidesTheConfiguredClient()
    {
        using var provider = AllOptionPaths(new RecordingConfigurator());

        var cache = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;

        cache.ConnectionMultiplexerFactory.Should().NotBeNull("the distributed cache opens no connection of its own");
        cache.ConfigurationOptions.Should().BeNull();
        cache.Configuration.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task WithAConfigurator_HealthCheckOwnConnection_RunsItBeforeConnecting()
    {
        // No AddRedisCaching here, so the PING check opens a connection of its own. The configurator
        // throws after recording, which proves it ran ahead of the connect and stops the connect.
        var configurator = new RecordingConfigurator(throwAfterRecording: true);
        var builder = Builder();
        builder.Services.AddRedisConfigurationOptionsConfigurator(configurator);
        builder.AddInfrastructureHealthChecks();
        await using var provider = builder.Services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(r => r.Name == "redis");
        var check = registration.Factory(provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration }, TestContext.Current.CancellationToken);

        result.Exception.Should().BeSameAs(configurator.Sentinel);
        var recorded = configurator.Calls.Should().ContainSingle().Subject;
        recorded.EndPoints.Select(EndPointCollection.ToString).Should().Equal("cache.example.invalid:6380");
        recorded.Ssl.Should().BeTrue();
        recorded.AbortOnConnectFail.Should().BeFalse("the options are parsed from the connection string");
    }

    /// <summary>Records every options instance it is handed, marks it, and optionally throws.</summary>
    private sealed class RecordingConfigurator(bool throwAfterRecording = false) : IRedisConfigurationOptionsConfigurator
    {
        public const string Mark = "configured-by-recording-configurator";

        public List<ConfigurationOptions> Calls { get; } = [];

        public InvalidOperationException Sentinel { get; } = new("recording configurator stop");

        public void Configure(ConfigurationOptions options)
        {
            lock (Calls)
            {
                Calls.Add(options);
            }

            options.ClientName = Mark;
            if (throwAfterRecording)
            {
                throw Sentinel;
            }
        }
    }
}
