using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MMCA.Common.Testing.Support;
using Xunit;
using Yarp.ReverseProxy.Configuration;

namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// Gateway edge-hardening fitness tests, authored once here and re-run as a thin sealed subclass per
/// gateway host (ADR-015). Covers what a host adopts from the shared gateway kit: the per-client-IP
/// rate limiter and its bypass list, an optional tighter named policy on the credential route, the
/// correlation ID stamped and echoed on every response, the per-downstream readiness checks, and the
/// active destination health probe on every cluster. The subclass supplies its own route table facts
/// (the limited path, the permit limits, the bypassed paths, the downstream service names); the
/// assertions and the test-server mechanics live here.
/// <para>
/// The rate-limit assertions go through <see cref="TestServer.SendAsync(Action{HttpContext}, CancellationToken)"/>
/// rather than an <see cref="HttpClient"/> because the limiter partitions on
/// <c>Connection.RemoteIpAddress</c>, which a <see cref="TestServer"/> request leaves null; the kit
/// deliberately fails OPEN on an unresolvable IP, so a client-driven test could never observe a 429.
/// Each test uses its own client IP (all inside the RFC 5737 TEST-NET-3 documentation range) so the
/// fixture's shared host gives every one a fresh window.
/// </para>
/// <para>
/// The subclass is expected to boot the real gateway in the Production environment with a
/// <see cref="RecordingHttpForwarder"/> swapped in for <c>IHttpForwarder</c>, so a proxied route
/// answers immediately instead of trying to reach a service-discovery name that resolves to nothing
/// in-process.
/// </para>
/// </summary>
/// <typeparam name="TEntryPoint">The gateway host's entry-point class, typically its <c>Program</c>.</typeparam>
public abstract class MmcaGatewayHardeningTestsBase<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>The forwarded caller whose own window the partitioning test exhausts.</summary>
    private const string ForwardedCallerA = "203.0.113.101";

    /// <summary>A second forwarded caller behind the same proxy IP, which must keep its own window.</summary>
    private const string ForwardedCallerB = "203.0.113.102";

    /// <summary>The client the throttle-exhaustion test burns its window as.</summary>
    private static readonly IPAddress GlobalThrottleClientIp = IPAddress.Parse("203.0.113.10");

    /// <summary>The client the bypass test burns its window as, so it cannot disturb the others.</summary>
    private static readonly IPAddress BypassProbeClientIp = IPAddress.Parse("203.0.113.20");

    /// <summary>The client the named-policy test uses, so its tighter window is independently observable.</summary>
    private static readonly IPAddress NamedPolicyClientIp = IPAddress.Parse("203.0.113.30");

    /// <summary>The connection-level address every forwarded-header request arrives from (the ingress proxy).</summary>
    private static readonly IPAddress ForwardedProxyIp = IPAddress.Parse("203.0.113.100");

    /// <summary>The booted gateway host under test, typically an xUnit class fixture's factory.</summary>
    protected abstract WebApplicationFactory<TEntryPoint> Factory { get; }

    /// <summary>
    /// The configured per-IP allowance of the global edge limiter, as the host's own configuration
    /// declares it. Stated by the subclass rather than read from the kit so an operator can see the
    /// number without reading the framework.
    /// </summary>
    protected abstract int PermitLimit { get; }

    /// <summary>A representative proxied route that IS subject to the global limiter.</summary>
    protected abstract string LimitedPath { get; }

    /// <summary>
    /// The downstream services the gateway fronts, one readiness check each. The check name is
    /// <see cref="DownstreamCheckPrefix"/> plus the service name.
    /// </summary>
    protected abstract IReadOnlyList<string> DownstreamServices { get; }

    /// <summary>
    /// Paths that must never be throttled. <c>/health</c> and <c>/.well-known</c> are always exempt
    /// inside the kit: probes and JWKS discovery run at high frequency by design, and throttling them
    /// turns a traffic spike into a failed liveness probe and a container restart. A host adds its own
    /// (a SignalR hub prefix, say) through its bypass configuration and lists them here.
    /// </summary>
    protected virtual IReadOnlyList<string> BypassedPaths =>
    [
        "/.well-known/jwks.json",
        "/health",
    ];

    /// <summary>
    /// A path on a route carrying a tighter named rate-limiter policy (the credential surface is the
    /// usual case), or null when the host declares no named policy. Null skips the named-policy test.
    /// </summary>
    protected virtual string? NamedPolicyPath => null;

    /// <summary>
    /// The per-IP allowance the <see cref="NamedPolicyPath"/> route carries on top of the global one.
    /// Ignored when <see cref="NamedPolicyPath"/> is null.
    /// </summary>
    protected virtual int NamedPolicyPermitLimit => 0;

    /// <summary>The active probe interval every cluster must carry, from the host's health-check defaults.</summary>
    protected virtual TimeSpan ActiveProbeInterval => TimeSpan.FromSeconds(30);

    /// <summary>The per-probe budget, normally left at the kit default.</summary>
    protected virtual TimeSpan ActiveProbeTimeout => TimeSpan.FromSeconds(5);

    /// <summary>
    /// The path the active probe hits. <c>/alive</c>, not <c>/health</c>: readiness on a downstream
    /// flips during its own rolling deployment, and ejecting a destination for that is the gateway
    /// treating a healthy deploy as an outage.
    /// </summary>
    protected virtual string ActiveProbePath => "/alive";

    /// <summary>The header the gateway correlation middleware stamps and echoes.</summary>
    protected virtual string CorrelationHeader => "X-Correlation-ID";

    /// <summary>The name prefix the per-downstream readiness checks are registered under.</summary>
    protected virtual string DownstreamCheckPrefix => "downstream-";

    /// <summary>The forwarded-client-IP header the edge honors ahead of the limiter.</summary>
    protected virtual string ForwardedForHeader => "X-Forwarded-For";

    [Fact]
    public async Task Route_IsThrottledOnceTheClientExhaustsItsWindow()
    {
        // Act: the configured allowance, then one more.
        var statusesWithinWindow = new List<int>();
        for (var i = 0; i < PermitLimit; i++)
        {
            statusesWithinWindow.Add(await SendAsync(LimitedPath, GlobalThrottleClientIp).ConfigureAwait(false));
        }

        var overflowStatus = await SendAsync(LimitedPath, GlobalThrottleClientIp).ConfigureAwait(false);

        // Assert: the edge sheds the excess rather than passing it to a backend. The limiter bounds
        // ANONYMOUS traffic too, which is the whole reason the edge limiter is not the service-side
        // per-user one.
        statusesWithinWindow.Should().AllSatisfy(status => status
            .Should().NotBe(StatusCodes.Status429TooManyRequests, "the configured allowance must be admitted in full"));
        overflowStatus.Should().Be(
            StatusCodes.Status429TooManyRequests,
            "the request past the per-IP window must be rejected at the edge");
    }

    [Fact]
    public async Task NamedPolicyRoute_IsThrottledAtItsOwnTighterAllowance()
    {
        // A declared dynamic skip would need xunit.v3.assert, which this shipped fixture library
        // deliberately does not reference: a host with no named policy simply passes.
        if (NamedPolicyPath is not { } namedPolicyPath)
        {
            return;
        }

        // Act: the named allowance, then one more. Both limiters see every one of these requests, so
        // this also proves the two compose rather than one replacing the other.
        var statusesWithinWindow = new List<int>();
        for (var i = 0; i < NamedPolicyPermitLimit; i++)
        {
            statusesWithinWindow.Add(await SendAsync(namedPolicyPath, NamedPolicyClientIp).ConfigureAwait(false));
        }

        var overflowStatus = await SendAsync(namedPolicyPath, NamedPolicyClientIp).ConfigureAwait(false);
        var otherRouteStatus = await SendAsync(LimitedPath, NamedPolicyClientIp).ConfigureAwait(false);

        // Assert
        statusesWithinWindow.Should().AllSatisfy(status => status
            .Should().NotBe(StatusCodes.Status429TooManyRequests, "the tighter allowance must be admitted in full"));
        overflowStatus.Should().Be(
            StatusCodes.Status429TooManyRequests,
            "the guarded surface must shed a burst well before the global window would");

        // The same client, same window, on a route without the named policy: still fine. That is what
        // makes the rejection above attributable to the ROUTE policy rather than to the global
        // limiter, which is nowhere near exhausted at one over the tighter allowance.
        otherRouteStatus.Should().NotBe(
            StatusCodes.Status429TooManyRequests,
            "the tighter policy must bind to its own route only, not throttle the caller everywhere");
    }

    [Fact]
    public async Task BypassedPaths_AreNotThrottledEvenAfterTheWindowIsExhausted()
    {
        BypassedPaths.Should().NotBeEmpty(
            "at least one exempt path must be declared, so the bypass list is actually verified");

        // Arrange: burn the whole window for this client on a limited route.
        for (var i = 0; i <= PermitLimit; i++)
        {
            await SendAsync(LimitedPath, BypassProbeClientIp).ConfigureAwait(false);
        }

        (await SendAsync(LimitedPath, BypassProbeClientIp).ConfigureAwait(false))
            .Should().Be(StatusCodes.Status429TooManyRequests, "the window must genuinely be exhausted first");

        foreach (var path in BypassedPaths)
        {
            // Act
            var status = await SendAsync(path, BypassProbeClientIp).ConfigureAwait(false);

            // Assert
            status.Should().NotBe(
                StatusCodes.Status429TooManyRequests,
                $"{path} is exempt from the edge limiter");
        }
    }

    [Fact]
    public async Task Response_CarriesAGeneratedCorrelationId_WhenTheCallerSuppliesNone()
    {
        // Arrange
        using var client = Factory.CreateClient();

        // Act
        using var response = await client.GetAsync(
            new Uri(LimitedPath, UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        // Assert: the edge mints one so the proxied request carries it downstream and the service-side
        // correlation middleware adopts it instead of minting a second.
        response.Headers.TryGetValues(CorrelationHeader, out var values)
            .Should().BeTrue("the gateway must stamp a correlation ID on every response");
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Response_EchoesTheCallerSuppliedCorrelationId()
    {
        // Arrange
        const string suppliedId = "8b1d0a0e-caller-supplied";
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(LimitedPath, UriKind.Relative));
        request.Headers.Add(CorrelationHeader, suppliedId);

        // Act
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);

        // Assert: a caller-supplied ID is never replaced, so a trace that started upstream survives the
        // hop through the edge.
        response.Headers.GetValues(CorrelationHeader).Single().Should().Be(suppliedId);
    }

    [Fact]
    public void Readiness_IncludesADownstreamCheckPerService()
    {
        DownstreamServices.Should().NotBeEmpty(
            "a gateway fronts at least one service, and each must report on reaching it");

        // Arrange
        var registrations = Factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        foreach (var serviceName in DownstreamServices)
        {
            // Act
            var registration = registrations.SingleOrDefault(r =>
                string.Equals(r.Name, DownstreamCheckPrefix + serviceName, StringComparison.Ordinal));

            // Assert: registered, and on the readiness endpoint only. A gateway that cannot reach its
            // services must be pulled out of the load balancer, but restarting the gateway process
            // fixes nothing about a downstream outage, so the check must never fail liveness.
            registration.Should().NotBeNull($"the gateway fronts {serviceName} and must report on reaching it");
            registration!.Tags.Should().Contain("ready");
            registration.Tags.Should().NotContain("live");
            registration.FailureStatus.Should().Be(
                HealthStatus.Unhealthy,
                "readiness is a binary routing decision and /health/ready treats Degraded as passing");
        }
    }

    [Fact]
    public async Task EveryCluster_CarriesAnActiveHealthCheckProbingAlive()
    {
        // Arrange
        var config = Factory.Services.GetRequiredService<IProxyConfigProvider>().GetConfig();
        var filters = Factory.Services.GetServices<IProxyConfigFilter>().ToArray();

        config.Clusters.Should().NotBeEmpty("the gateway must front at least one cluster");

        foreach (var rawCluster in config.Clusters)
        {
            // The provider hands out the PRE-filter config, so the defaults only appear after the
            // registered filter chain has run, which is what YARP's config manager does at load time.
            var cluster = rawCluster;
            foreach (var filter in filters)
            {
                cluster = await filter
                    .ConfigureClusterAsync(cluster, TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
            }

            // Assert: passive checks only demote a destination after real traffic has already failed
            // against it, so a restarting service keeps absorbing requests until enough of them error.
            // The active probe polls out of band and ejects the destination first.
            var active = cluster.HealthCheck?.Active;
            active.Should().NotBeNull($"cluster '{cluster.ClusterId}' must carry an active health check");
            active!.Enabled.Should().BeTrue($"cluster '{cluster.ClusterId}' declares the block but leaves it off");

            active.Path.Should().Be(
                ActiveProbePath,
                $"cluster '{cluster.ClusterId}' must probe liveness, not readiness");
            active.Interval.Should().Be(ActiveProbeInterval);
            active.Timeout.Should().Be(ActiveProbeTimeout);
        }
    }

    [Fact]
    public async Task RateLimiter_PartitionsByForwardedClientIp_NotByProxyIp()
    {
        // Arrange: every request arrives from the SAME connection IP (the ingress proxy in production,
        // where the platform terminates the public connection) while the forwarded-for header names
        // the real caller. The forwarded-headers middleware must run BEFORE the limiter, or all users
        // share one window.
        for (var i = 0; i <= PermitLimit; i++)
        {
            await SendForwardedAsync(LimitedPath, ForwardedProxyIp, ForwardedCallerA).ConfigureAwait(false);
        }

        // Sanity: caller A's window is genuinely exhausted.
        (await SendForwardedAsync(LimitedPath, ForwardedProxyIp, ForwardedCallerA).ConfigureAwait(false))
            .Should().Be(StatusCodes.Status429TooManyRequests, "caller A's own window must be exhausted first");

        // Act
        var callerBStatus = await SendForwardedAsync(LimitedPath, ForwardedProxyIp, ForwardedCallerB).ConfigureAwait(false);

        // Assert: a different forwarded caller behind the same proxy IP has its own window.
        callerBStatus.Should().NotBe(
            StatusCodes.Status429TooManyRequests,
            "partitioning must follow the forwarded client IP, not the shared proxy connection IP");
    }

    /// <summary>
    /// Issues one request through the test server with an explicit client IP, returning the status
    /// code. The IP is what the edge limiter partitions on.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="clientIp">The client IP to attribute the request to.</param>
    /// <returns>The response status code.</returns>
    protected async Task<int> SendAsync(string path, IPAddress clientIp)
    {
        var context = await Factory.Server.SendAsync(
            ctx =>
            {
                ctx.Request.Method = HttpMethods.Get;
                ctx.Request.Scheme = Uri.UriSchemeHttps;
                ctx.Request.Path = path;
                ctx.Connection.RemoteIpAddress = clientIp;
            },
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        return context.Response.StatusCode;
    }

    /// <summary>
    /// Issues one request attributed to a proxy connection IP with a forwarded-for caller, returning
    /// the status code.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="proxyIp">The connection-level IP (the ingress proxy).</param>
    /// <param name="forwardedFor">The forwarded client IP header value.</param>
    /// <returns>The response status code.</returns>
    protected async Task<int> SendForwardedAsync(string path, IPAddress proxyIp, string forwardedFor)
    {
        var forwardedHeader = ForwardedForHeader;
        var context = await Factory.Server.SendAsync(
            ctx =>
            {
                ctx.Request.Method = HttpMethods.Get;
                ctx.Request.Scheme = Uri.UriSchemeHttps;
                ctx.Request.Path = path;
                ctx.Request.Headers[forwardedHeader] = forwardedFor;
                ctx.Connection.RemoteIpAddress = proxyIp;
            },
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        return context.Response.StatusCode;
    }
}
