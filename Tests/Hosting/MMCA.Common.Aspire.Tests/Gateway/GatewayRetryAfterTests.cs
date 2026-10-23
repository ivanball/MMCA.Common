using System.Globalization;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// The edge limiter's 429 tells the caller when its per-IP window resets: <c>AddGatewayRateLimiting</c>
/// installs an <c>OnRejected</c> that writes <c>Retry-After</c> from the rejecting lease, and keeps
/// any callback the host configured before it.
/// </summary>
public sealed class GatewayRetryAfterTests
{
    [Fact]
    public async Task AddGatewayRateLimiting_APerIpRejectionGetsRetryAfter()
    {
        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(new GatewayRateLimitingSettings { PermitLimit = 1, WindowSeconds = 30 });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var context = ContextFor();

        using var first = await options.GlobalLimiter!.AcquireAsync(context, 1, CancellationToken.None);
        using var rejected = await options.GlobalLimiter.AcquireAsync(context, 1, CancellationToken.None);
        rejected.IsAcquired.Should().BeFalse();

        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        int.Parse(context.Response.Headers.RetryAfter.ToString(), CultureInfo.InvariantCulture)
            .Should().BeInRange(1, 30, "the hint is the time left in the 30-second window, rounded up");
    }

    [Fact]
    public async Task AddGatewayRateLimiting_KeepsAnOnRejectedTheHostConfiguredFirst()
    {
        var previousRan = false;
        var services = new ServiceCollection();
        services.AddRateLimiter(options => options.OnRejected = (_, _) =>
        {
            previousRan = true;
            return ValueTask.CompletedTask;
        });
        services.AddGatewayRateLimiting(new GatewayRateLimitingSettings { PermitLimit = 1, WindowSeconds = 30 });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        var context = ContextFor();

        using var first = await options.GlobalLimiter!.AcquireAsync(context, 1, CancellationToken.None);
        using var rejected = await options.GlobalLimiter.AcquireAsync(context, 1, CancellationToken.None);
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        previousRan.Should().BeTrue();
        context.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
    }

    private static DefaultHttpContext ContextFor()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/orders";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        return context;
    }
}
