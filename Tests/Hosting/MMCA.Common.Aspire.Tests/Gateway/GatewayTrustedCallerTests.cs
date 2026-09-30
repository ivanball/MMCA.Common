using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// The trusted-internal-caller exemption: a generalization of the synthetic-traffic bypass, added
/// so a server-rendered UI host's back-end calls (token refreshes above all) are not collapsed into
/// one client-IP partition and throttled as if they were a flood.
/// </summary>
public sealed class GatewayTrustedCallerTests
{
    private const string ConfiguredSecret = "trusted-internal-caller-secret-for-tests-0";

    private static DefaultHttpContext Ctx(string? headerName = null, params string[] headerValues)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/Auth/refresh";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.7");

        if (headerName is not null)
        {
            context.Request.Headers[headerName] = headerValues;
        }

        return context;
    }

    [Fact]
    public void Settings_DefaultToTheExemptionBeingOff()
    {
        var settings = new GatewayRateLimitingSettings();

        settings.TrustedCallerHeaderName.Should().Be("X-Internal-Caller-Key");
        settings.TrustedCallerSecret.Should().BeNull(
            because: "the exemption must be unavailable until a host configures a secret");
    }

    [Fact]
    public void IsTrustedInternalCaller_WithNoSecretConfigured_IsAlwaysFalse()
    {
        var settings = new GatewayRateLimitingSettings();

        GatewayRateLimitingExtensions
            .IsTrustedInternalCaller(Ctx("X-Internal-Caller-Key", "anything"), settings)
            .Should().BeFalse();
    }

    [Fact]
    public void IsTrustedInternalCaller_WithTheCorrectSecret_IsTrue()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };

        GatewayRateLimitingExtensions
            .IsTrustedInternalCaller(Ctx("X-Internal-Caller-Key", ConfiguredSecret), settings)
            .Should().BeTrue();
    }

    [Fact]
    public void IsTrustedInternalCaller_WithAWrongOrAbsentSecret_IsFalse()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };

        GatewayRateLimitingExtensions.IsTrustedInternalCaller(Ctx(), settings).Should().BeFalse();
        GatewayRateLimitingExtensions
            .IsTrustedInternalCaller(Ctx("X-Internal-Caller-Key", "wrong"), settings)
            .Should().BeFalse();
    }

    [Fact]
    public void IsTrustedInternalCaller_RefusesAMultiValuedHeader()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };

        GatewayRateLimitingExtensions
            .IsTrustedInternalCaller(Ctx("X-Internal-Caller-Key", "wrong", ConfiguredSecret), settings)
            .Should().BeFalse(because: "a caller must not be able to spray candidates in one header");
    }

    [Fact]
    public void BothLimiterPartitions_HonourTheProvenTrustedCaller()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };
        var context = Ctx("X-Internal-Caller-Key", ConfiguredSecret);

        GatewayRateLimitingExtensions.ClientIpPartition(context, settings).PartitionKey.Should().Be("__bypass");
        GatewayRateLimitingExtensions.ConcurrencyPartition(context, settings).PartitionKey.Should().Be("__bypass");
    }

    // M177: the proof is handed to the named per-route policies of MMCA.Common.Gateway, which read
    // this exact key (they cannot see these settings).
    [Fact]
    public void ClientIpPartition_ForAProvenTrustedCaller_MarksTheRequestForTheRoutePolicies()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };
        var context = Ctx("X-Internal-Caller-Key", ConfiguredSecret);

        GatewayRateLimitingExtensions.ClientIpPartition(context, settings);

        context.Items["MMCA.Common.Gateway.TrustedInternalCaller"].Should().Be(true);
    }

    [Fact]
    public void ClientIpPartition_ForAnUnprovenCaller_LeavesTheRequestUnmarked()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };
        var context = Ctx("X-Internal-Caller-Key", "wrong-secret");

        GatewayRateLimitingExtensions.ClientIpPartition(context, settings);

        context.Items.ContainsKey("MMCA.Common.Gateway.TrustedInternalCaller").Should().BeFalse();
    }

    [Fact]
    public void BothLimiterPartitions_StillLimitAnUnprovenCaller()
    {
        var settings = new GatewayRateLimitingSettings { TrustedCallerSecret = ConfiguredSecret };
        var context = Ctx();

        GatewayRateLimitingExtensions.ClientIpPartition(context, settings).PartitionKey.Should().Be("198.51.100.7");
        GatewayRateLimitingExtensions.ConcurrencyPartition(context, settings).PartitionKey.Should().Be("__gateway");
    }

    [Fact]
    public void TheSyntheticTrafficBypass_KeepsWorkingAlongsideIt()
    {
        var settings = new GatewayRateLimitingSettings
        {
            SyntheticTrafficSecret = "synthetic-traffic-secret-for-tests-0000000",
            TrustedCallerSecret = ConfiguredSecret
        };

        GatewayRateLimitingExtensions
            .ClientIpPartition(Ctx("X-Synthetic-Traffic-Key", "synthetic-traffic-secret-for-tests-0000000"), settings)
            .PartitionKey.Should().Be("__bypass");
    }
}
