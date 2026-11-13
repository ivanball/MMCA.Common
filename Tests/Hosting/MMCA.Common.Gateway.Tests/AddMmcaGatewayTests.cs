using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.Configuration;
using MMCA.Common.Gateway.Forwarding;
using MMCA.Common.Gateway.Transforms;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MMCA.Common.Gateway.Tests;

/// <summary>
/// Unit tests for the composition entry point: what a single <c>AddMmcaGateway</c> call actually
/// puts in the container, and that the settings bind from the <c>MmcaGateway</c> section.
/// </summary>
public sealed class AddMmcaGatewayTests
{
    [Fact]
    public void AddMmcaGateway_RegistersBothConfigFiltersAndTheTransformProvider()
    {
        var services = new ServiceCollection();

        // AddReverseProxy pulls in YARP's own policy types, which take ILogger<T> constructor
        // arguments, so the container needs logging before any of them can be activated.
        services.AddLogging();
        services.AddReverseProxy().AddMmcaGateway(new GatewaySettings());

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IProxyConfigFilter>().Select(f => f.GetType())
            .Should().Contain([
                typeof(GatewayClusterProfileConfigFilter),
                typeof(GatewayHealthCheckDefaultsConfigFilter),
            ]);

        provider.GetServices<ITransformProvider>()
            .Should().ContainSingle(p => p is GatewayTraceHeaderTransformProvider);
    }

    [Fact]
    public void AddMmcaGateway_FromSettings_MakesThemResolvable()
    {
        var settings = new GatewaySettings
        {
            ClusterRequestDefaults = new GatewayClusterRequestProfile { Version = "2.0" },
        };

        var services = new ServiceCollection();
        services.AddReverseProxy().AddMmcaGateway(settings);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<GatewaySettings>>().Value
            .ClusterRequestDefaults!.Version.Should().Be("2.0");
    }

    [Fact]
    public void AddMmcaGateway_FromConfiguration_BindsTheMmcaGatewaySection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MmcaGateway:ClusterRequestDefaults:Version"] = "2.0",
                ["MmcaGateway:ClusterRequestDefaults:VersionPolicy"] = "RequestVersionExact",
                ["MmcaGateway:ClusterRequestDefaults:ActivityTimeout"] = "00:01:40",
                ["MmcaGateway:ClusterRequestOverrides:notification-hub:ActivityTimeout"] = "01:00:00",
                ["MmcaGateway:TraceHeaders:RouteHeaderName"] = "X-Edge-Route",
                ["MmcaGateway:HealthCheckDefaults:Passive:ReactivationPeriod"] = "00:02:00",
                ["MmcaGateway:RateLimiterPolicies:auth-tight:PermitLimit"] = "5",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddReverseProxy().AddMmcaGateway(configuration);

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<GatewaySettings>>().Value;

        settings.ClusterRequestDefaults!.Version.Should().Be("2.0");
        settings.ClusterRequestDefaults.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(100));
        settings.ClusterRequestOverrides["notification-hub"].ActivityTimeout.Should().Be(TimeSpan.FromHours(1));
        settings.TraceHeaders.RouteHeaderName.Should().Be("X-Edge-Route");
        settings.HealthCheckDefaults.Passive.ReactivationPeriod.Should().Be(TimeSpan.FromMinutes(2));
        settings.RateLimiterPolicies["auth-tight"].PermitLimit.Should().Be(5);
    }

    [Fact]
    public void AddMmcaGateway_WithNoSection_UsesTheDocumentedDefaults()
    {
        var services = new ServiceCollection();
        services.AddReverseProxy().AddMmcaGateway(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<GatewaySettings>>().Value;

        settings.ClusterRequestDefaults.Should().BeNull();
        settings.HealthCheckDefaults.Passive.Enabled.Should().BeTrue();
        settings.TraceHeaders.Enabled.Should().BeTrue();
        settings.RateLimiterPolicies.Should().BeEmpty();
        GatewaySettings.SectionName.Should().Be("MmcaGateway");
    }

    // Item 31, rule 1: with the retry off (the default) YARP's own forwarder factory stays in use.
    [Fact]
    public void RetryDisabled_KeepsYarpsFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddReverseProxy().AddMmcaGateway(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IForwarderHttpClientFactory>().Should().BeOfType<ForwarderHttpClientFactory>();
        services.Should().NotContain(d => d.ImplementationType == typeof(GatewayRetryForwarderHttpClientFactory));
    }

    // Item 31, rule 2: with the retry on, through either overload, the retrying factory wins over the
    // one AddReverseProxy registered, and a TimeProvider is available to it.
    [Fact]
    public void RetryEnabled_RegistersTheRetryingFactory()
    {
        var fromConfiguration = new ServiceCollection();
        fromConfiguration.AddLogging();
        fromConfiguration.AddReverseProxy().AddMmcaGateway(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MmcaGateway:Retry:Enabled"] = "true" })
            .Build());

        var fromSettings = new ServiceCollection();
        fromSettings.AddLogging();
        fromSettings.AddReverseProxy().AddMmcaGateway(
            new GatewaySettings { Retry = new GatewayRetrySettings { Enabled = true } });

        foreach (var services in new[] { fromConfiguration, fromSettings })
        {
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<IForwarderHttpClientFactory>()
                .Should().BeOfType<GatewayRetryForwarderHttpClientFactory>();
            provider.GetRequiredService<TimeProvider>().Should().NotBeNull();
        }
    }

    // Item 31, rule 3: a host that already composed its own forwarder factory is told, loudly, rather
    // than having it silently replaced.
    [Fact]
    public void RetryEnabled_WithAForeignFactory_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IForwarderHttpClientFactory, ForeignForwarderHttpClientFactory>();

        var act = () => services.AddReverseProxy().AddMmcaGateway(
            new GatewaySettings { Retry = new GatewayRetrySettings { Enabled = true } });

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain(nameof(ForeignForwarderHttpClientFactory))
            .And.Contain("MmcaGateway:Retry:Enabled");
    }

    // The package must stay usable by a gateway that never took AddServiceDefaults, so it may not
    // drag the Aspire graph in behind the consumer's back.
    [Fact]
    public void GatewayAssembly_DoesNotDependOnMmcaCommonAspire() =>
        typeof(GatewaySettings).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should().NotContain("MMCA.Common.Aspire");

    /// <summary>A host's own forwarder factory, standing in for any composition the retry must not overwrite.</summary>
    private sealed class ForeignForwarderHttpClientFactory : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            throw new NotSupportedException("never called by these tests");
    }
}
