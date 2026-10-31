using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.RateLimiting;
using MMCA.Common.UI.Web.Hardening;

namespace MMCA.Common.UI.Web.Tests.Hardening;

/// <summary>
/// The Blazor host's limiter answers a per-IP rejection with a <c>Retry-After</c> naming when the
/// caller's window resets, written by the <c>OnRejected</c> callback <c>AddUiRateLimiting</c> installs.
/// </summary>
public sealed class UiRateLimitingRetryAfterTests
{
    [Fact]
    public async Task AddUiRateLimiting_APerIpRejectionGetsRetryAfter()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{UiRateLimitingSettings.SectionName}:Enabled"] = "true",
                [$"{UiRateLimitingSettings.SectionName}:PermitLimit"] = "1",
                [$"{UiRateLimitingSettings.SectionName}:WindowSeconds"] = "30",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUiRateLimiting(configuration);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");

        using var first = await options.GlobalLimiter!.AcquireAsync(context, 1, CancellationToken.None);
        using var rejected = await options.GlobalLimiter.AcquireAsync(context, 1, CancellationToken.None);
        rejected.IsAcquired.Should().BeFalse();

        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        int.Parse(context.Response.Headers.RetryAfter.ToString(), CultureInfo.InvariantCulture)
            .Should().BeInRange(1, 30, "the hint is the time left in the 30-second window, rounded up");
    }

    [Fact]
    public async Task ClientIpPartition_AMidWindowRejection_RetryAfterIsTheTimeLeft_NotTheWindow()
    {
        // The framework fixed window always reported the whole window; the partition now builds the
        // remaining-window limiter, so a rejection 18 seconds into a 30-second window says 12.
        var settings = new UiRateLimitingSettings { Enabled = true, PermitLimit = 1, WindowSeconds = 30 };
        var time = new FakeTimeProvider();
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var partition = UiRateLimitingExtensions.ClientIpPartition(context, settings, new PathString("/api"), time);
        await using var limiter = partition.Factory(partition.PartitionKey);
        limiter.Should().BeOfType<RemainingWindowFixedWindowRateLimiter>();

        using var first = limiter.AttemptAcquire();
        time.Advance(TimeSpan.FromSeconds(18));
        using var rejected = limiter.AttemptAcquire();

        first.IsAcquired.Should().BeTrue();
        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(12));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{UiRateLimitingSettings.SectionName}:Enabled"] = "true",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUiRateLimiting(configuration);
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        context.Response.Headers.RetryAfter.ToString().Should().Be("12");
    }
}
