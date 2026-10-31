using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Gateway.RateLimiting;
using MMCA.Common.RateLimiting;

namespace MMCA.Common.Gateway.Tests.RateLimiting;

/// <summary>
/// The named route policies answer a mid-window rejection with the time left in the window, not the
/// whole window (the shared limiter source file the package links in).
/// </summary>
public sealed class GatewayRoutePolicyRetryAfterTests
{
    [Fact]
    public async Task Partition_AMidWindowRejection_RetryAfterIsTheTimeLeft_NotTheWindow()
    {
        var policy = new GatewayRoutePolicySettings { PermitLimit = 1, WindowSeconds = 30 };
        var time = new FakeTimeProvider();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        var partition = GatewayRoutePolicyExtensions.Partition(context, policy, time);
        await using var limiter = partition.Factory(partition.PartitionKey);
        limiter.Should().BeOfType<RemainingWindowFixedWindowRateLimiter>();
        ((ReplenishingRateLimiter)limiter).IsAutoReplenishing.Should().BeFalse(
            "the middleware's partitioned endpoint limiter replenishes every partition on its own timer");

        using var first = limiter.AttemptAcquire();
        time.Advance(TimeSpan.FromSeconds(18));
        using var rejected = limiter.AttemptAcquire();

        first.IsAcquired.Should().BeTrue();
        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(12));

        var services = new ServiceCollection();
        services.AddGatewayRoutePolicies(new GatewaySettings
        {
            RateLimiterPolicies = new Dictionary<string, GatewayRoutePolicySettings>(StringComparer.Ordinal)
            {
                ["auth-tight"] = policy,
            },
        });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        context.Response.Headers.RetryAfter.ToString().Should().Be("12");
    }
}
