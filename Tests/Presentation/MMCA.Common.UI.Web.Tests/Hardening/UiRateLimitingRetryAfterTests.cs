using System.Globalization;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
}
