using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.Configuration;
using Yarp.ReverseProxy.Configuration;

namespace MMCA.Common.Gateway.Tests.Configuration;

/// <summary>
/// Unit tests for the destination health-check defaults filter. The contract worth pinning is that
/// it only ever FILLS A GAP: a cluster that states its own passive or active block keeps it byte for
/// byte, because an operator who wrote one meant it.
/// </summary>
public sealed class GatewayHealthCheckDefaultsConfigFilterTests
{
    [Fact]
    public async Task PassiveDefaults_AreAppliedToAClusterWithNoHealthCheckBlock()
    {
        var result = await ConfigureAsync(new GatewaySettings(), Cluster("identity"));

        result.HealthCheck!.Passive!.Enabled.Should().BeTrue();
        result.HealthCheck.Passive.Policy.Should().Be("TransportFailureRate");
        result.HealthCheck.Passive.ReactivationPeriod.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ActiveDefaults_AreOffUnlessTheHostTurnsThemOn()
    {
        var result = await ConfigureAsync(new GatewaySettings(), Cluster("identity"));

        result.HealthCheck!.Active.Should().BeNull(
            because: "an extra probe per destination per interval is real traffic and real cost");
    }

    [Fact]
    public async Task ActiveDefaults_AreAppliedWhenEnabled()
    {
        var settings = new GatewaySettings
        {
            HealthCheckDefaults = new GatewayHealthCheckDefaults
            {
                Active = new GatewayActiveHealthCheckDefaults { Enabled = true },
            },
        };

        var result = await ConfigureAsync(settings, Cluster("identity"));

        result.HealthCheck!.Active!.Enabled.Should().BeTrue();
        result.HealthCheck.Active.Policy.Should().Be("ConsecutiveFailures");
        result.HealthCheck.Active.Path.Should().Be("/alive",
            because: "probing readiness makes a downstream's own rolling deployment look like an outage");
    }

    [Fact]
    public async Task ExplicitPassiveBlock_IsLeftExactlyAsWritten()
    {
        var declared = new PassiveHealthCheckConfig
        {
            Enabled = true,
            Policy = "SomeCustomPolicy",
            ReactivationPeriod = TimeSpan.FromMinutes(15),
        };

        var cluster = Cluster("identity") with { HealthCheck = new HealthCheckConfig { Passive = declared } };

        var result = await ConfigureAsync(new GatewaySettings(), cluster);

        result.HealthCheck!.Passive.Should().BeSameAs(declared);
    }

    // An operator who explicitly disabled passive checks on one cluster must not have them switched
    // back on by the defaults: Enabled=false is a written block, not an absent one.
    [Fact]
    public async Task ExplicitlyDisabledPassiveBlock_StaysDisabled()
    {
        var cluster = Cluster("identity") with
        {
            HealthCheck = new HealthCheckConfig { Passive = new PassiveHealthCheckConfig { Enabled = false } },
        };

        var result = await ConfigureAsync(new GatewaySettings(), cluster);

        result.HealthCheck!.Passive!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task ExplicitActiveBlock_IsLeftExactlyAsWritten()
    {
        var declared = new ActiveHealthCheckConfig { Enabled = true, Path = "/custom-probe" };
        var cluster = Cluster("identity") with { HealthCheck = new HealthCheckConfig { Active = declared } };

        var settings = new GatewaySettings
        {
            HealthCheckDefaults = new GatewayHealthCheckDefaults
            {
                Active = new GatewayActiveHealthCheckDefaults { Enabled = true, Path = "/alive" },
            },
        };

        var result = await ConfigureAsync(settings, cluster);

        result.HealthCheck!.Active.Should().BeSameAs(declared);
    }

    [Fact]
    public async Task AvailableDestinationsPolicy_SurvivesTheFill()
    {
        var cluster = Cluster("identity") with
        {
            HealthCheck = new HealthCheckConfig { AvailableDestinationsPolicy = "HealthyAndUnknown" },
        };

        var result = await ConfigureAsync(new GatewaySettings(), cluster);

        result.HealthCheck!.AvailableDestinationsPolicy.Should().Be("HealthyAndUnknown");
        result.HealthCheck.Passive.Should().NotBeNull();
    }

    [Fact]
    public async Task NothingToFill_ReturnsTheClusterUntouched()
    {
        var settings = new GatewaySettings
        {
            HealthCheckDefaults = new GatewayHealthCheckDefaults
            {
                Passive = new GatewayPassiveHealthCheckDefaults { Enabled = false },
            },
        };

        var cluster = Cluster("identity");
        var filter = new GatewayHealthCheckDefaultsConfigFilter(Options.Create(settings));

        var result = await filter.ConfigureClusterAsync(cluster, CancellationToken.None);

        result.Should().BeSameAs(cluster);
    }

    [Fact]
    public async Task Routes_AreLeftUntouched()
    {
        var route = new RouteConfig { RouteId = "identity-auth", ClusterId = "identity" };
        var filter = new GatewayHealthCheckDefaultsConfigFilter(Options.Create(new GatewaySettings()));

        var result = await filter.ConfigureRouteAsync(route, cluster: null, CancellationToken.None);

        result.Should().BeSameAs(route);
    }

    private static async Task<ClusterConfig> ConfigureAsync(GatewaySettings settings, ClusterConfig cluster) =>
        await new GatewayHealthCheckDefaultsConfigFilter(Options.Create(settings))
            .ConfigureClusterAsync(cluster, CancellationToken.None);

    private static ClusterConfig Cluster(string clusterId) => new()
    {
        ClusterId = clusterId,
        Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
        {
            ["primary"] = new() { Address = "http://" + clusterId },
        },
    };
}
