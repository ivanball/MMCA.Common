using System.Net;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.RateLimiting;
using MMCA.Common.Shared.Resilience;

namespace MMCA.Common.Gateway.Tests.RateLimiting;

/// <summary>
/// The route-policy limiter's 429 carries a <c>Retry-After</c>, and the package's internal copy of
/// the header logic (it references no MMCA package) produces exactly what the Shared helper the other
/// MMCA limiters use produces, so the two cannot drift.
/// </summary>
public sealed class GatewayRetryAfterTests
{
    public static TheoryData<long> HintsInMilliseconds => new() { 0, 1, 999, 1_000, 1_001, 4_000, 59_750, 60_000, -250 };

    [Theory]
    [MemberData(nameof(HintsInMilliseconds))]
    public void Format_MatchesTheSharedHelper(long milliseconds)
    {
        var hint = TimeSpan.FromMilliseconds(milliseconds);

        GatewayRetryAfter.Format(hint).Should().Be(RetryAfterHeader.Format(hint));
    }

    [Fact]
    public async Task AddGatewayRoutePolicies_ARejectedRequestGetsRetryAfter()
    {
        var policy = new GatewayRoutePolicySettings { PermitLimit = 1, WindowSeconds = 30 };
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

        await using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            httpContext => GatewayRoutePolicyExtensions.Partition(httpContext, policy));
        var context = ContextFor(IPAddress.Parse("198.51.100.4"));
        using var first = await limiter.AcquireAsync(context, 1, CancellationToken.None);
        using var rejected = await limiter.AcquireAsync(context, 1, CancellationToken.None);
        rejected.IsAcquired.Should().BeFalse();

        options.OnRejected.Should().NotBeNull();
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        var header = context.Response.Headers.RetryAfter.ToString();
        int.Parse(header, System.Globalization.CultureInfo.InvariantCulture).Should().BeInRange(1, 30);
    }

    [Fact]
    public async Task OnRejected_WithoutARetryAfterHint_WritesNoHeader()
    {
        var context = ContextFor(IPAddress.Parse("198.51.100.4"));
        using var lease = new HintlessLease();

        await GatewayRetryAfter.WithRetryAfterHeader(previous: null)(
            new OnRejectedContext { HttpContext = context, Lease = lease }, CancellationToken.None);

        context.Response.Headers.RetryAfter.Should().BeEmpty();
    }

    [Fact]
    public async Task OnRejected_ChainsTheCallbackConfiguredBeforeIt()
    {
        var context = ContextFor(IPAddress.Parse("198.51.100.4"));
        using var lease = new HintlessLease();
        var previousRan = false;

        await GatewayRetryAfter.WithRetryAfterHeader((_, _) =>
        {
            previousRan = true;
            return ValueTask.CompletedTask;
        })(new OnRejectedContext { HttpContext = context, Lease = lease }, CancellationToken.None);

        previousRan.Should().BeTrue();
    }

    private static DefaultHttpContext ContextFor(IPAddress remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }

    private sealed class HintlessLease : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
