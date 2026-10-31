using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Aspire.Gateway;
using MMCA.Common.RateLimiting;

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

    [Fact]
    public async Task ClientIpPartition_AMidWindowRejection_RetryAfterIsTheTimeLeft_NotTheWindow()
    {
        // Store local test run 2 (X-18) saw Retry-After: 60 from this limiter mid-window: the framework
        // fixed window always reports the whole window. The partition now builds the remaining-window
        // limiter, so a rejection 18 seconds into a 30-second window says 12.
        var settings = new GatewayRateLimitingSettings { PermitLimit = 1, WindowSeconds = 30 };
        var time = new FakeTimeProvider();
        var context = ContextFor();
        var partition = GatewayRateLimitingExtensions.ClientIpPartition(context, settings, time);
        await using var limiter = partition.Factory(partition.PartitionKey);
        limiter.Should().BeOfType<RemainingWindowFixedWindowRateLimiter>();

        using var first = limiter.AttemptAcquire();
        time.Advance(TimeSpan.FromSeconds(18));
        using var rejected = limiter.AttemptAcquire();

        first.IsAcquired.Should().BeTrue();
        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(12));

        var services = new ServiceCollection();
        services.AddGatewayRateLimiting(settings);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        context.Response.Headers.RetryAfter.ToString().Should().Be("12");
    }

    private static DefaultHttpContext ContextFor()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/orders";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        return context;
    }
}
