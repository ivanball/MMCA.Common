using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Caching;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Tests;

/// <summary>
/// The Redis configurator extension point on the Infrastructure side (security lane D1): with no
/// configurator registered the SignalR backplane connects with exactly the options its connection
/// string describes (plus the per-application channel prefix it always had), and a registered
/// configurator runs once on the backplane's own options before anything connects. Nothing here
/// opens a connection: only the built options are read.
/// </summary>
public sealed class DependencyInjectionRedisConfiguratorTests
{
    private const string RedisConnectionString = "cache.example.invalid:6380,ssl=true,abortConnect=false,connectTimeout=1000";

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PushNotifications:Enabled"] = "true",
                ["PushNotifications:HubPath"] = "/hubs/test",
                ["ConnectionStrings:redis"] = RedisConnectionString,
            })
            .Build();

    [Fact]
    public void Backplane_WithNoConfigurator_HasExactlyTheConnectionStringOptions()
    {
        var services = new ServiceCollection();
        services.AddPushNotifications(Configuration());
        using var provider = services.BuildServiceProvider();

        var actual = provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;

        var expected = ConfigurationOptions.Parse(RedisConnectionString);
        expected.ChannelPrefix = actual.ChannelPrefix;
        actual.ToString(includePassword: true).Should().Be(expected.ToString(includePassword: true));
        actual.ChannelPrefix.IsNullOrEmpty.Should().BeFalse("the backplane keeps its per-application channel prefix");
    }

    [Fact]
    public void Backplane_WithAConfigurator_RunsItOnceOnTheBackplaneOptions()
    {
        var configurator = new RecordingConfigurator();
        var services = new ServiceCollection();
        services.AddRedisConfigurationOptionsConfigurator(configurator);
        services.AddPushNotifications(Configuration());
        using var provider = services.BuildServiceProvider();

        var actual = provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;
        _ = provider.GetRequiredService<IOptions<RedisOptions>>().Value;

        configurator.Calls.Should().ContainSingle().Which.Should().BeSameAs(actual);
        actual.ClientName.Should().Be(RecordingConfigurator.Mark, "the configured options are the ones the backplane uses");
        actual.EndPoints.Select(EndPointCollection.ToString).Should().Equal("cache.example.invalid:6380");
        actual.Ssl.Should().BeTrue();
    }

    [Fact]
    public void Backplane_ConfiguratorRegisteredAfterAddPushNotifications_StillRuns()
    {
        var configurator = new RecordingConfigurator();
        var services = new ServiceCollection();
        services.AddPushNotifications(Configuration());
        services.AddRedisConfigurationOptionsConfigurator(configurator);
        using var provider = services.BuildServiceProvider();

        var actual = provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration;

        configurator.Calls.Should().ContainSingle().Which.Should().BeSameAs(actual);
    }

    [Fact]
    public void ConfigurationOptionsPipeline_WithAConfigurator_RunsIt()
    {
        // The options pipeline the Aspire connection points read: a configurator registered through the
        // Infrastructure call reaches IOptions<ConfigurationOptions> consumers.
        var configurator = new RecordingConfigurator();
        var services = new ServiceCollection();
        services.AddRedisConfigurationOptionsConfigurator(configurator);
        services.Configure<ConfigurationOptions>(options => options.EndPoints.Add("cache.example.invalid", 6380));
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ConfigurationOptions>>().Value;

        configurator.Calls.Should().ContainSingle().Which.Should().BeSameAs(options);
        options.ClientName.Should().Be(RecordingConfigurator.Mark);
    }

    [Fact]
    public void AddRedisConfigurationOptionsConfigurator_RejectsNull()
    {
        var act = () => new ServiceCollection().AddRedisConfigurationOptionsConfigurator(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Records every options instance it is handed and marks it.</summary>
    private sealed class RecordingConfigurator : IRedisConfigurationOptionsConfigurator
    {
        public const string Mark = "configured-by-recording-configurator";

        public List<ConfigurationOptions> Calls { get; } = [];

        public void Configure(ConfigurationOptions options)
        {
            lock (Calls)
            {
                Calls.Add(options);
            }

            options.ClientName = Mark;
        }
    }
}
