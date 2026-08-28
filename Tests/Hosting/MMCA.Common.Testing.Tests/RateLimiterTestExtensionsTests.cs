using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Unit tests for <c>NeutralizeGlobalRateLimiter</c>: the helper every per-service integration test
/// factory calls so a suite that drives dozens of requests through one host is not throttled by the
/// production budget. The load-bearing part is that it wins over the host's own registration no
/// matter which ran first, which is what the PostConfigure ordering buys.
/// </summary>
public sealed class RateLimiterTestExtensionsTests
{
    [Fact]
    public async Task NeutralizeGlobalRateLimiter_OverridesAHostLimiterThatWouldRejectEveryRequest()
    {
        // Arrange: a host limiter with no permits at all, so any surviving limiter rejects.
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<RateLimiterOptions>(options =>
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                _ => RateLimitPartition.GetFixedWindowLimiter(
                    "host",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromHours(1), QueueLimit = 0 })));

        // Act
        services.NeutralizeGlobalRateLimiter();

        // Assert: every acquisition succeeds, including well past the host's single permit.
        var limiter = services.BuildServiceProvider()
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
        limiter.Should().NotBeNull("the neutralizer must install a limiter, not clear the option");

        for (var i = 0; i < 5; i++)
        {
            using var lease = await limiter!.AcquireAsync(
                new DefaultHttpContext(), 1, TestContext.Current.CancellationToken);
            lease.IsAcquired.Should().BeTrue("the neutralized limiter must admit every request");
        }
    }

    [Fact]
    public void NeutralizeGlobalRateLimiter_ReturnsTheSameCollection_ForChaining()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var returned = services.NeutralizeGlobalRateLimiter();

        // Assert
        returned.Should().BeSameAs(services);
    }

    [Fact]
    public async Task NeutralizeGlobalRateLimiter_AppliesEvenWhenTheHostConfiguresItsLimiterAfterwards()
    {
        // Arrange: the neutralizer runs FIRST here, and the host's own Configure lands after it. A
        // plain Configure would then be overwritten; the PostConfigure this helper uses still wins,
        // which is the whole reason a test factory can call it without ordering ceremony.
        var services = new ServiceCollection();
        services.AddOptions();
        services.NeutralizeGlobalRateLimiter("integration-tests");
        services.Configure<RateLimiterOptions>(options =>
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                _ => RateLimitPartition.GetConcurrencyLimiter(
                    "host",
                    _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 })));

        // Act
        var limiter = services.BuildServiceProvider()
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;

        // Assert: two concurrent leases both succeed, which the host's one-permit limiter would refuse.
        using var first = await limiter!.AcquireAsync(new DefaultHttpContext(), 1, TestContext.Current.CancellationToken);
        using var second = await limiter.AcquireAsync(new DefaultHttpContext(), 1, TestContext.Current.CancellationToken);
        first.IsAcquired.Should().BeTrue();
        second.IsAcquired.Should().BeTrue();
    }
}
