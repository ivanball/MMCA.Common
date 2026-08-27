using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// Unit tests for the gateway's downstream health checks: what gets registered (names, tags,
/// failure status), that a repeated registration does not produce the duplicate check name the
/// health-check service rejects at startup, and how the probe itself maps an HTTP outcome onto a
/// health status.
/// </summary>
public sealed class GatewayDownstreamHealthChecksTests
{
    [Fact]
    public void AddGatewayDownstreamHealthChecks_RegistersOneCheckPerService()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks("catalog", "sales");

        Registrations(services).Select(r => r.Name)
            .Should().BeEquivalentTo("downstream-catalog", "downstream-sales");
    }

    // The tag choice is the whole readiness contract: /health/ready includes every check NOT tagged
    // live or optional, and /alive includes only the live-tagged ones. A downstream outage must pull
    // the gateway out of the load balancer WITHOUT restarting its container, because restarting the
    // gateway fixes nothing about a service that is down.
    [Fact]
    public void DownstreamChecks_AreTaggedReady_SoTheyGateReadinessButNotLiveness()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks("catalog");

        var registration = Registrations(services).Single();

        registration.Tags.Should().Contain(HealthCheckTags.Ready);
        registration.Tags.Should().NotContain(HealthCheckTags.Live);
        registration.Tags.Should().NotContain(HealthCheckTags.Optional);
    }

    // Unhealthy, not Degraded: /health/ready treats Degraded as passing, so a Degraded downstream
    // check would report a problem while the gateway kept taking traffic it cannot serve.
    [Fact]
    public void DownstreamChecks_FailAsUnhealthy() =>
        RegistrationFor("catalog").FailureStatus.Should().Be(HealthStatus.Unhealthy);

    [Fact]
    public void DownstreamChecks_UseAShortProbeBudget() =>
        RegistrationFor("catalog").Timeout.Should().Be(TimeSpan.FromSeconds(2));

    [Fact]
    public void AddGatewayDownstreamHealthChecks_RegistersAServiceDiscoveryClientPerService()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks("catalog");

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(GatewayHealthCheckExtensions.ClientName("catalog"));

        client.BaseAddress.Should().Be(new Uri("http://catalog"),
            because: "service discovery rewrites the scheme-and-name form into the real endpoint");
        client.Timeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    // An HttpClient left on its own defaults sends HTTP/1.1, which an Http2-only cleartext (h2c)
    // endpoint refuses outright, so the probe fails and the gateway reports a downstream outage that
    // does not exist. The services a modular-monolith gateway fronts serve h2c precisely so that
    // cross-service gRPC works without TLS/ALPN, which makes HTTP/2 the right default here.
    [Fact]
    public void ProbeClient_SpeaksHttp2ByDefault()
    {
        var client = ProbeClientFor("catalog");

        client.DefaultRequestVersion.Should().Be(HttpVersion.Version20);
        client.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionExact,
            because: "h2c prior knowledge means the request must go out as HTTP/2, not negotiate down");
    }

    [Fact]
    public void ProbeClient_CanOptOutBackToHttp11PerDownstream()
    {
        var client = ProbeClientFor("legacy", configure: o => o.ProbeOverHttp2 = false);

        client.DefaultRequestVersion.Should().Be(HttpVersion.Version11);
        client.DefaultVersionPolicy.Should().Be(HttpVersionPolicy.RequestVersionOrLower);
    }

    // The opt-out is per CALL, so a gateway fronting a mix of profiles registers each group once and
    // the second call must not retro-change the first group's client.
    [Fact]
    public void ProbeClient_VersionProfileIsScopedToItsOwnRegistrationCall()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks("catalog");
        services.AddGatewayDownstreamHealthChecks(o => o.ProbeOverHttp2 = false, "legacy");

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var http2Client = factory.CreateClient(GatewayHealthCheckExtensions.ClientName("catalog"));
        using var http11Client = factory.CreateClient(GatewayHealthCheckExtensions.ClientName("legacy"));

        http2Client.DefaultRequestVersion.Should().Be(HttpVersion.Version20);
        http11Client.DefaultRequestVersion.Should().Be(HttpVersion.Version11);
    }

    [Fact]
    public void AddGatewayDownstreamHealthChecks_WithOptions_RegistersTheSameChecks()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks(o => o.ProbeOverHttp2 = false, "catalog", "sales");

        Registrations(services).Select(r => r.Name)
            .Should().BeEquivalentTo("downstream-catalog", "downstream-sales");
    }

    [Fact]
    public void Options_DefaultToHttp2() =>
        new GatewayDownstreamHealthCheckOptions().ProbeOverHttp2.Should().BeTrue();

    private static HttpClient ProbeClientFor(
        string serviceName,
        Action<GatewayDownstreamHealthCheckOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks(configure, serviceName);

        return services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(GatewayHealthCheckExtensions.ClientName(serviceName));
    }

    // A duplicate health-check NAME is a startup exception, not a harmless second registration, so
    // a host and a module both asking for the same downstream must not compound.
    [Fact]
    public void AddGatewayDownstreamHealthChecks_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks("catalog", "sales");
        services.AddGatewayDownstreamHealthChecks("CATALOG", "sales", "identity");

        Registrations(services).Select(r => r.Name)
            .Should().BeEquivalentTo("downstream-catalog", "downstream-sales", "downstream-identity");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddGatewayDownstreamHealthChecks_IgnoresBlankNames(string serviceName)
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks(serviceName);

        Registrations(services).Should().BeEmpty();
    }

    [Fact]
    public async Task Probe_WhenDownstreamAnswersAlive_ReportsHealthy()
    {
        var result = await ProbeAsync(_ => new HttpResponseMessage(HttpStatusCode.OK));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Probe_WhenDownstreamAnswersNonSuccess_ReportsUnhealthy()
    {
        var result = await ProbeAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("503");
    }

    [Fact]
    public async Task Probe_WhenDownstreamIsUnreachable_ReportsUnhealthyWithTheException()
    {
        var result = await ProbeAsync(_ => throw new HttpRequestException("no route to host"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Probe_WhenTheProbeTimesOut_ReportsUnhealthy()
    {
        var result = await ProbeAsync(_ => throw new TaskCanceledException("probe budget exhausted"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Probe_TargetsAliveNotHealthReady()
    {
        Uri? requested = null;
        await ProbeAsync(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        requested!.AbsolutePath.Should().Be("/alive",
            because: "probing readiness would make every downstream rolling deployment look like a gateway failure");
    }

    private static async Task<HealthCheckResult> ProbeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using var handler = new StubHandler(respond);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };

        var check = new DownstreamServiceHealthCheck(new StubHttpClientFactory(client), "catalog", "client");
        var registration = RegistrationFor("catalog");

        return await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration },
            CancellationToken.None);
    }

    private static HealthCheckRegistration RegistrationFor(string serviceName)
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks(serviceName);
        return Registrations(services).Single();
    }

    // GetService, not GetRequiredService: AddHealthChecks() alone does not pull in the options
    // infrastructure, so a collection with zero registered checks has no IOptions<> at all.
    private static IReadOnlyList<HealthCheckRegistration> Registrations(IServiceCollection services)
    {
        var options = services.BuildServiceProvider().GetService<IOptions<HealthCheckServiceOptions>>();
        return options is null ? [] : [.. options.Value.Registrations];
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
