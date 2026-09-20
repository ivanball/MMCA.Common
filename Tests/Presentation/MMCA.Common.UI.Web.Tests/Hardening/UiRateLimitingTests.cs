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
/// Pins the Blazor host's own edge rate limiter (SEC-Store-56, extracted from the copies MMCA.ADC
/// and MMCA.Store each carried): <c>AddUiRateLimiting</c> really registers a global limiter that
/// rejects with 429, it partitions by client IP, and it leaves health probes and static assets
/// alone.
/// </summary>
public sealed class UiRateLimitingTests
{
    /// <summary>
    /// The load-bearing registration assertion: before this kit a public Blazor origin called
    /// neither <c>AddRateLimiter</c> nor anything equivalent, so an unauthenticated flood reached
    /// the Blazor pipeline unmetered. Resolving the options that only the limiter registers proves
    /// the wiring is present, not just that the partition helpers compile.
    /// </summary>
    [Fact]
    public void AddUiRateLimiting_RegistersTheEdgeRateLimiter()
    {
        var limiterOptions = ResolveLimiterOptions(enabled: true);

        // Both assertions fail on an unconfigured host: GlobalLimiter defaults to null and
        // RejectionStatusCode defaults to 503.
        limiterOptions.GlobalLimiter.Should().NotBeNull(
            "the chained per-IP window and concurrency ceiling are registered as the global limiter");
        limiterOptions.RejectionStatusCode.Should().Be((int)HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// The escape hatch has to actually read the section: with <c>Enabled: false</c> no global
    /// limiter is installed, while the rejection code stays 429 so a host that re-enables it mid
    /// flight does not start answering 503.
    /// </summary>
    [Fact]
    public void AddUiRateLimiting_WhenDisabled_InstallsNoGlobalLimiter()
    {
        var limiterOptions = ResolveLimiterOptions(enabled: false);

        limiterOptions.GlobalLimiter.Should().BeNull();
        limiterOptions.RejectionStatusCode.Should().Be((int)HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// A page route and the SignalR negotiate endpoint (which is what opens a circuit) must be
    /// counted; probes, framework assets, hub traffic and static files must not, or a traffic spike
    /// would fail liveness and a single visitor's asset-heavy first page load would exhaust their
    /// own window.
    /// </summary>
    [Theory]
    [InlineData("/health", true)]
    [InlineData("/alive", true)]
    [InlineData("/_framework/blazor.web.js", true)]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css", true)]
    [InlineData("/hubs/notifications", true)]
    [InlineData("/app.css", true)]
    [InlineData("/", false)]
    [InlineData("/login", false)]
    [InlineData("/_blazor", false)]
    [InlineData("/_blazor/negotiate", false)]
    [InlineData("/healthz", false)]
    public void IsExempt_SeparatesInfrastructureFromPages(string path, bool expected) =>
        UiRateLimitingExtensions.IsExempt(new PathString(path)).Should().Be(expected);

    /// <summary>
    /// Two different callers must land in two different windows, and one caller must land in the
    /// same window twice: a partition key that ignored the IP would throttle the whole site as one
    /// bucket.
    /// </summary>
    [Fact]
    public void ClientIpPartition_KeysOnTheCaller()
    {
        var settings = new UiRateLimitingSettings();

        var first = UiRateLimitingExtensions.ClientIpPartition(ContextFor("/", "203.0.113.7"), settings);
        var firstAgain = UiRateLimitingExtensions.ClientIpPartition(ContextFor("/cart", "203.0.113.7"), settings);
        var second = UiRateLimitingExtensions.ClientIpPartition(ContextFor("/", "203.0.113.8"), settings);

        first.PartitionKey.Should().Be(firstAgain.PartitionKey);
        first.PartitionKey.Should().NotBe(second.PartitionKey);
    }

    /// <summary>
    /// An exempt path takes the no-limiter partition on BOTH chained limiters, not just the per-IP
    /// one: the concurrency ceiling would otherwise still queue a liveness probe behind a flood.
    /// </summary>
    [Fact]
    public void ExemptPath_TakesTheNoLimiterPartitionOnBothLimiters()
    {
        var settings = new UiRateLimitingSettings();
        var probe = ContextFor("/alive", "203.0.113.7");
        var page = ContextFor("/", "203.0.113.7");

        UiRateLimitingExtensions.ClientIpPartition(probe, settings).PartitionKey
            .Should().NotBe(UiRateLimitingExtensions.ClientIpPartition(page, settings).PartitionKey);
        UiRateLimitingExtensions.ConcurrencyPartition(probe, settings).PartitionKey
            .Should().NotBe(UiRateLimitingExtensions.ConcurrencyPartition(page, settings).PartitionKey);
    }

    private static RateLimiterOptions ResolveLimiterOptions(bool enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{UiRateLimitingSettings.SectionName}:Enabled"] = enabled ? "true" : "false",
                [$"{UiRateLimitingSettings.SectionName}:PermitLimit"] = "300",
                [$"{UiRateLimitingSettings.SectionName}:WindowSeconds"] = "60",
                [$"{UiRateLimitingSettings.SectionName}:GlobalConcurrencyLimit"] = "200",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddUiRateLimiting(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
    }

    private static DefaultHttpContext ContextFor(string path, string clientIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = new PathString(path);
        context.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
        return context;
    }
}
