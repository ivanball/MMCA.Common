using AwesomeAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Aspire.Kestrel;

namespace MMCA.Common.Aspire.Tests.Kestrel;

/// <summary>
/// The shared Kestrel endpoint profile: endpoint defaults on one protocol set, plus a dedicated
/// Http1-only listener for the platform health probes when <c>HealthProbe:Port</c> is configured.
/// The listener plan is what decides whether a deployed revision answers its probes at all, so it is
/// asserted directly rather than through a bound server.
/// </summary>
public sealed class KestrelEndpointExtensionsTests
{
    private static IConfiguration Config(string? probePort)
    {
        var values = new Dictionary<string, string?>();
        if (probePort is not null)
        {
            values["HealthProbe:Port"] = probePort;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void NoProbePortConfigured_DeclaresNoExplicitListener()
    {
        var plan = KestrelEndpointExtensions.BuildListenerPlan(
            Config(null),
            HttpProtocols.Http2,
            redeclareCleartextEndpoint: true,
            KestrelEndpointExtensions.DefaultCleartextPort);

        plan.Should().BeEmpty(
            "locally there is no probe port, and an explicit Listen call would override the dynamic ports Aspire assigns");
    }

    [Fact]
    public void BlankProbePort_IsTreatedAsAbsent()
    {
        var plan = KestrelEndpointExtensions.BuildListenerPlan(
            Config(string.Empty),
            HttpProtocols.Http2,
            redeclareCleartextEndpoint: true,
            KestrelEndpointExtensions.DefaultCleartextPort);

        plan.Should().BeEmpty();
    }

    [Fact]
    public void UnparseableProbePort_FailsFast()
    {
        var act = () => KestrelEndpointExtensions.BuildListenerPlan(
            Config("eighty-eighty-two"),
            HttpProtocols.Http2,
            redeclareCleartextEndpoint: true,
            KestrelEndpointExtensions.DefaultCleartextPort);

        act.Should().Throw<InvalidOperationException>(
            "a mistyped probe port that silently produced no listener would leave the platform probing a closed port");
    }

    /// <summary>
    /// The REST/gRPC profile: h2c everywhere, and because an explicit Listen call replaces the
    /// container's default binding, the main cleartext endpoint has to be re-declared next to the
    /// probe listener.
    /// </summary>
    [Fact]
    public void Http2Profile_RedeclaresTheCleartextEndpointAndAddsAnHttp1ProbeListener()
    {
        var plan = KestrelEndpointExtensions.BuildListenerPlan(
            Config("8082"),
            HttpProtocols.Http2,
            redeclareCleartextEndpoint: true,
            KestrelEndpointExtensions.DefaultCleartextPort);

        plan.Should().Equal(
            new KestrelEndpointExtensions.KestrelListenerSpec(8080, HttpProtocols.Http2),
            new KestrelEndpointExtensions.KestrelListenerSpec(8082, HttpProtocols.Http1));
    }

    /// <summary>
    /// The mixed profile (a SignalR host whose endpoints come from configuration): the probe listener
    /// is strictly additive, because re-declaring 8080 would collide with the config-declared
    /// endpoint that already owns it.
    /// </summary>
    [Fact]
    public void MixedProfile_AddsOnlyTheProbeListener()
    {
        var plan = KestrelEndpointExtensions.BuildListenerPlan(
            Config("8082"),
            HttpProtocols.Http1AndHttp2,
            redeclareCleartextEndpoint: false,
            KestrelEndpointExtensions.DefaultCleartextPort);

        plan.Should().Equal(
            new KestrelEndpointExtensions.KestrelListenerSpec(8082, HttpProtocols.Http1));
    }

    [Fact]
    public void ProbeListener_IsAlwaysHttp1_EvenWhenTheDefaultsAreHttp2()
    {
        var plan = KestrelEndpointExtensions.BuildListenerPlan(
            Config("9090"),
            HttpProtocols.Http2,
            redeclareCleartextEndpoint: true,
            KestrelEndpointExtensions.DefaultCleartextPort);

        plan[^1].Protocols.Should().Be(
            HttpProtocols.Http1,
            "the platform httpGet probes speak HTTP/1.1, which an Http2-only endpoint rejects with GOAWAY HTTP_1_1_REQUIRED");
    }

    [Fact]
    public void CleartextPort_IsHonoured_WhenTheHostDoesNotUseTheContainerDefault()
    {
        var plan = KestrelEndpointExtensions.BuildListenerPlan(
            Config("8082"),
            HttpProtocols.Http2,
            redeclareCleartextEndpoint: true,
            cleartextPort: 5000);

        plan[0].Port.Should().Be(5000);
    }
}
