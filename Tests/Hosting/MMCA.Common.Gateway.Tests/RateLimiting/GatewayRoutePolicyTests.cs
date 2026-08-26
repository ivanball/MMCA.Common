using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.RateLimiting;

namespace MMCA.Common.Gateway.Tests.RateLimiting;

/// <summary>
/// Unit tests for the named per-route rate-limiter policies a YARP route references through its own
/// <c>RateLimiterPolicy</c> property.
/// </summary>
public sealed class GatewayRoutePolicyTests
{
    [Fact]
    public void Partition_ForAClientIpPolicy_KeysOnTheCallerAddress()
    {
        var partition = GatewayRoutePolicyExtensions.Partition(
            ContextFor(IPAddress.Parse("203.0.113.7")),
            new GatewayRoutePolicySettings());

        partition.PartitionKey.Should().Be("203.0.113.7");
    }

    [Fact]
    public void Partition_ForAGlobalPolicy_SharesOneBucket()
    {
        var settings = new GatewayRoutePolicySettings { Partition = GatewayRoutePolicyPartition.Global };

        var first = GatewayRoutePolicyExtensions.Partition(ContextFor(IPAddress.Loopback), settings);
        var second = GatewayRoutePolicyExtensions.Partition(ContextFor(IPAddress.Any), settings);

        first.PartitionKey.Should().Be(second.PartitionKey);
    }

    // Failing open beats collapsing every unattributable request into one shared bucket, which
    // throttles an in-process TestServer to a standstill.
    [Fact]
    public void Partition_WithUnresolvableIp_FailsOpen() =>
        GatewayRoutePolicyExtensions.Partition(ContextFor(remoteIp: null), new GatewayRoutePolicySettings())
            .PartitionKey.Should().Be(GatewayRoutePolicySettings.GlobalPartitionKey);

    [Fact]
    public void AddGatewayRoutePolicies_RegistersA429Rejection()
    {
        var services = new ServiceCollection();
        services.AddGatewayRoutePolicies(SettingsWith("auth-tight", new GatewayRoutePolicySettings()));

        services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value
            .RejectionStatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    // The policy map is not publicly readable, so the registration is proved the way ASP.NET Core
    // itself reports it: a second policy under the same name is a duplicate and throws.
    [Fact]
    public void AddGatewayRoutePolicies_RegistersThePolicyUnderItsConfiguredName()
    {
        var services = new ServiceCollection();
        services.AddGatewayRoutePolicies(SettingsWith("auth-tight", new GatewayRoutePolicySettings()));
        services.AddGatewayRoutePolicies(SettingsWith("auth-tight", new GatewayRoutePolicySettings()));

        var act = () => services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddGatewayRoutePolicies_WithNoPolicies_RegistersNothing()
    {
        var services = new ServiceCollection();
        services.AddGatewayRoutePolicies(new GatewaySettings());

        services.Should().BeEmpty(
            because: "a gateway that declared no named policies must not be given a rate limiter it never asked for");
    }

    // ADR-070 fail-fast: an out-of-range budget is a startup failure, not a surprise at the first
    // throttled request in production.
    [Fact]
    public void AddGatewayRoutePolicies_WithAnOutOfRangeBudget_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        var settings = SettingsWith("auth-tight", new GatewayRoutePolicySettings { PermitLimit = 0 });

        var act = () => services.AddGatewayRoutePolicies(settings);

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public async Task NamedPolicy_ExhaustsItsWindowAndThenRejects()
    {
        var policy = new GatewayRoutePolicySettings { PermitLimit = 2, WindowSeconds = 60 };

        // Exercised through the partition the policy hands ASP.NET Core, which is the same object
        // the rate-limiting middleware acquires a lease against.
        await using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => GatewayRoutePolicyExtensions.Partition(httpContext, policy));

        var context = ContextFor(IPAddress.Parse("198.51.100.4"));

        using var first = await limiter.AcquireAsync(context, 1, CancellationToken.None);
        using var second = await limiter.AcquireAsync(context, 1, CancellationToken.None);
        using var third = await limiter.AcquireAsync(context, 1, CancellationToken.None);

        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeTrue();
        third.IsAcquired.Should().BeFalse();
    }

    [Fact]
    public void Settings_DefaultsAreTheEdgeShape()
    {
        var settings = new GatewayRoutePolicySettings();

        settings.Partition.Should().Be(GatewayRoutePolicyPartition.ClientIp);
        settings.PermitLimit.Should().Be(30);
        settings.WindowSeconds.Should().Be(60);
        settings.QueueLimit.Should().Be(0, because: "a queue at the edge hides a throttle as latency");
    }

    private static GatewaySettings SettingsWith(string name, GatewayRoutePolicySettings policy) => new()
    {
        RateLimiterPolicies = new Dictionary<string, GatewayRoutePolicySettings>(StringComparer.Ordinal)
        {
            [name] = policy,
        },
    };

    private static DefaultHttpContext ContextFor(IPAddress? remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }
}
