using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.Configuration;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.Gateway.Tests.Configuration;

/// <summary>
/// Unit tests for the cluster request-profile filter: the three-source precedence that lets a
/// gateway declare the h2c profile once, and the rollback switch that takes every HTTP/2 cluster
/// back to negotiated forwarding in one configuration change.
/// </summary>
public sealed class GatewayClusterProfileConfigFilterTests
{
    private static readonly GatewayClusterRequestProfile Http2Profile = new()
    {
        Version = "2.0",
        VersionPolicy = "RequestVersionExact",
        ActivityTimeout = TimeSpan.FromSeconds(100),
    };

    [Fact]
    public async Task Defaults_ApplyToAClusterThatDeclaresNothing()
    {
        var config = await ConfigureAsync(
            new GatewaySettings { ClusterRequestDefaults = Http2Profile },
            Cluster("identity"));

        config.Version.Should().Be(HttpVersion.Version20);
        config.VersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionExact);
        config.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    // The whole point of the shared defaults: three clusters, one declaration.
    [Fact]
    public async Task Defaults_ApplyToEveryClusterAlike()
    {
        var settings = new GatewaySettings { ClusterRequestDefaults = Http2Profile };

        foreach (var clusterId in new[] { "identity", "conference", "engagement" })
        {
            var config = await ConfigureAsync(settings, Cluster(clusterId));
            config.Version.Should().Be(HttpVersion.Version20);
        }
    }

    [Fact]
    public async Task PerClusterOverride_BeatsTheDefaults()
    {
        var settings = new GatewaySettings
        {
            ClusterRequestDefaults = Http2Profile,
            ClusterRequestOverrides = new Dictionary<string, GatewayClusterRequestProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["notification-hub"] = new()
                {
                    Version = "1.1",
                    VersionPolicy = "RequestVersionOrLower",
                    ActivityTimeout = TimeSpan.FromHours(1),
                },
            },
        };

        var config = await ConfigureAsync(settings, Cluster("notification-hub"));

        config.Version.Should().Be(HttpVersion.Version11,
            because: "a SignalR hub opens with an HTTP/1.1 Upgrade handshake");
        config.VersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
        config.ActivityTimeout.Should().Be(TimeSpan.FromHours(1));
    }

    // Per PROPERTY, not per block: an override that only lengthens the timeout must still inherit
    // the shared version pair, or every override becomes a second copy of the defaults.
    [Fact]
    public async Task PerClusterOverride_MergesPropertyByProperty()
    {
        var settings = new GatewaySettings
        {
            ClusterRequestDefaults = Http2Profile,
            ClusterRequestOverrides = new Dictionary<string, GatewayClusterRequestProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["engagement"] = new() { ActivityTimeout = TimeSpan.FromHours(1) },
            },
        };

        var config = await ConfigureAsync(settings, Cluster("engagement"));

        config.ActivityTimeout.Should().Be(TimeSpan.FromHours(1));
        config.Version.Should().Be(HttpVersion.Version20);
        config.VersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionExact);
    }

    [Fact]
    public async Task ClusterOwnConfig_BeatsBothOverrideAndDefaults()
    {
        var settings = new GatewaySettings
        {
            ClusterRequestDefaults = Http2Profile,
            ClusterRequestOverrides = new Dictionary<string, GatewayClusterRequestProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["identity"] = new() { Version = "1.1" },
            },
        };

        var cluster = Cluster("identity") with
        {
            HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(5) },
        };

        var config = await ConfigureAsync(settings, cluster);

        config.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(5));
        config.Version.Should().Be(HttpVersion.Version11, because: "the override still wins over the defaults");
    }

    [Fact]
    public async Task RollbackSwitch_StripsTheHttp2ProfileFromEveryCluster()
    {
        var config = await ConfigureAsync(
            new GatewaySettings { ForwardHttp2 = false, ClusterRequestDefaults = Http2Profile },
            Cluster("identity"));

        config.Version.Should().BeNull();
        config.VersionPolicy.Should().BeNull();
        config.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(100),
            because: "the rollback is about the wire protocol, not about the request budget");
    }

    [Fact]
    public async Task RollbackSwitch_StripsAClusterThatDeclaredHttp2Itself()
    {
        var cluster = Cluster("identity") with
        {
            HttpRequest = new ForwarderRequestConfig
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            },
        };

        var config = await ConfigureAsync(new GatewaySettings { ForwardHttp2 = false }, cluster);

        config.Version.Should().BeNull();
        config.VersionPolicy.Should().BeNull();
    }

    // Clearing a 1.1 cluster would hand it back to YARP's HTTP/2-preferring default, which is the
    // opposite of what a rollback is for.
    [Fact]
    public async Task RollbackSwitch_LeavesAnHttp11ClusterAlone()
    {
        var settings = new GatewaySettings
        {
            ForwardHttp2 = false,
            ClusterRequestOverrides = new Dictionary<string, GatewayClusterRequestProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["notification-rest"] = new() { Version = "1.1", VersionPolicy = "RequestVersionOrLower" },
            },
        };

        var config = await ConfigureAsync(settings, Cluster("notification-rest"));

        config.Version.Should().Be(HttpVersion.Version11);
        config.VersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Fact]
    public async Task RollbackSwitch_DefaultsToOn()
    {
        var config = await ConfigureAsync(
            new GatewaySettings { ClusterRequestDefaults = Http2Profile },
            Cluster("identity"));

        config.Version.Should().Be(HttpVersion.Version20);
    }

    [Fact]
    public async Task ClusterWithNoProfileAnywhere_IsReturnedUntouched()
    {
        var cluster = Cluster("identity");
        var filter = Filter(new GatewaySettings());

        var result = await filter.ConfigureClusterAsync(cluster, CancellationToken.None);

        result.Should().BeSameAs(cluster);
        result.HttpRequest.Should().BeNull();
    }

    [Fact]
    public async Task InvalidVersion_FailsWithTheClusterNamed()
    {
        var settings = new GatewaySettings
        {
            ClusterRequestDefaults = new GatewayClusterRequestProfile { Version = "two" },
        };

        var act = async () => await ConfigureAsync(settings, Cluster("identity"));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*identity*");
    }

    [Fact]
    public async Task InvalidVersionPolicy_FailsWithTheClusterNamed()
    {
        var settings = new GatewaySettings
        {
            ClusterRequestDefaults = new GatewayClusterRequestProfile { VersionPolicy = "Whatever" },
        };

        var act = async () => await ConfigureAsync(settings, Cluster("identity"));

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*identity*");
    }

    [Fact]
    public async Task Routes_AreLeftUntouched()
    {
        var route = new RouteConfig { RouteId = "identity-auth", ClusterId = "identity" };
        var filter = Filter(new GatewaySettings { ClusterRequestDefaults = Http2Profile });

        var result = await filter.ConfigureRouteAsync(route, cluster: null, CancellationToken.None);

        result.Should().BeSameAs(route);
    }

    private static async Task<ForwarderRequestConfig> ConfigureAsync(GatewaySettings settings, ClusterConfig cluster)
    {
        var result = await Filter(settings).ConfigureClusterAsync(cluster, CancellationToken.None);
        return result.HttpRequest!;
    }

    private static GatewayClusterProfileConfigFilter Filter(GatewaySettings settings) =>
        new(Options.Create(settings));

    private static ClusterConfig Cluster(string clusterId) => new()
    {
        ClusterId = clusterId,
        Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
        {
            ["primary"] = new() { Address = "http://" + clusterId },
        },
    };
}
