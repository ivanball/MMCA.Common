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
/// health-check service rejects at startup, how the probe itself maps an HTTP outcome onto a health
/// status, and how it works out which HTTP version a downstream speaks (negotiate once, latch the
/// answer, never latch on an outage).
/// </summary>
public sealed class GatewayDownstreamHealthChecksTests
{
    /// <summary>What one h2c prior-knowledge attempt looks like on the wire.</summary>
    private static readonly ProbeAttempt Http2Attempt =
        new(HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact);

    /// <summary>What one stock HTTP/1.1 attempt looks like on the wire.</summary>
    private static readonly ProbeAttempt Http11Attempt =
        new(HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower);

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

    // The probe client carries NO version pin: the version belongs to the REQUEST, because an Auto
    // probe can put HTTP/2 and HTTP/1.1 through this one client on a single poll while it works out
    // which one the downstream speaks.
    [Fact]
    public void ProbeClient_DoesNotPinAnHttpVersion()
    {
        var client = ProbeClientFor("catalog");
        using var stock = new HttpClient();

        client.DefaultRequestVersion.Should().Be(stock.DefaultRequestVersion);
        client.DefaultVersionPolicy.Should().Be(stock.DefaultVersionPolicy);
    }

    // The version profile is per CALL, so a gateway pinning one head's profile must not retro-change
    // the group an earlier call registered. Driven through the REGISTRATION rather than a hand-built
    // check, so the option is proven to travel from AddGatewayDownstreamHealthChecks onto the wire.
    [Fact]
    public async Task ProbeVersion_IsScopedToItsOwnRegistrationCall()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks("catalog");
        services.AddGatewayDownstreamHealthChecks(o => o.ProbeVersion = DownstreamProbeVersion.Http11, "legacy");

        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };

        // Registered last, so it wins over the real factory AddHttpClient put in earlier.
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(client));

        await using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        foreach (var registration in registrations)
        {
            var check = registration.Factory(provider);
            await check.CheckHealthAsync(
                new HealthCheckContext { Registration = registration },
                CancellationToken.None);
        }

        handler.Attempts.Should().Equal(Http2Attempt, Http11Attempt);
    }

    [Fact]
    public void AddGatewayDownstreamHealthChecks_WithOptions_RegistersTheSameChecks()
    {
        var services = new ServiceCollection();
        services.AddGatewayDownstreamHealthChecks(
            o => o.ProbeVersion = DownstreamProbeVersion.Http11, "catalog", "sales");

        Registrations(services).Select(r => r.Name)
            .Should().BeEquivalentTo("downstream-catalog", "downstream-sales");
    }

    [Fact]
    public void Options_DefaultToAutoNegotiation() =>
        new GatewayDownstreamHealthCheckOptions().ProbeVersion.Should().Be(DownstreamProbeVersion.Auto);

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

    // Auto is the default, so the first attempt of every probe goes out as h2c prior knowledge and
    // an endpoint that answers it settles the question: later polls send one request, not two.
    [Fact]
    public async Task Probe_WhenAutoAndHttp2Answers_LatchesHttp2AndStopsNegotiating()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        var first = await check.CheckHealthAsync(context, CancellationToken.None);
        var second = await check.CheckHealthAsync(context, CancellationToken.None);

        first.Status.Should().Be(HealthStatus.Healthy);
        second.Status.Should().Be(HealthStatus.Healthy);
        handler.Attempts.Should().Equal(Http2Attempt, Http2Attempt);
    }

    // A cleartext Http1AndHttp2 endpoint without ALPN answers HTTP_1_1_REQUIRED forever. The check
    // resolves that itself, inside ONE poll, which is what removes the consumer's manual opt-out.
    [Theory]
    [InlineData(HttpRequestError.VersionNegotiationError)]
    [InlineData(HttpRequestError.HttpProtocolError)]
    public async Task Probe_WhenAutoAndHttp2IsRefused_FallsBackToHttp11WithinTheSameCheck(
        HttpRequestError error)
    {
        using var handler = new StubHandler(RefusesHttp2(error));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        var result = await check.CheckHealthAsync(context, CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy,
            because: "the fallback answered inside the same check, so the poll has a verdict");
        handler.Attempts.Should().Equal(Http2Attempt, Http11Attempt);
    }

    [Fact]
    public async Task Probe_WhenAutoHasFallenBack_GoesStraightToHttp11()
    {
        using var handler = new StubHandler(RefusesHttp2(HttpRequestError.VersionNegotiationError));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        await check.CheckHealthAsync(context, CancellationToken.None);
        var later = await check.CheckHealthAsync(context, CancellationToken.None);

        later.Status.Should().Be(HealthStatus.Healthy);
        handler.Attempts.Should().Equal(Http2Attempt, Http11Attempt, Http11Attempt);
    }

    // Latching the version that answered must not launder the answer: a 503 over the fallback is
    // still a downstream failure.
    [Fact]
    public async Task Probe_WhenAutoFallbackAnswersNonSuccess_ReportsUnhealthyAndStillLatches()
    {
        using var handler = new StubHandler(request => request.Version == HttpVersion.Version20
            ? throw new HttpRequestException(HttpRequestError.VersionNegotiationError, "HTTP_1_1_REQUIRED")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        var first = await check.CheckHealthAsync(context, CancellationToken.None);
        var later = await check.CheckHealthAsync(context, CancellationToken.None);

        first.Status.Should().Be(HealthStatus.Unhealthy);
        first.Description.Should().Contain("503");
        later.Status.Should().Be(HealthStatus.Unhealthy);
        handler.Attempts.Should().Equal(Http2Attempt, Http11Attempt, Http11Attempt);
    }

    // A downstream that is simply DOWN must never pin a protocol: the outage says nothing about
    // which version the endpoint speaks, and the wrong latch would outlive the outage.
    [Fact]
    public async Task Probe_WhenAutoAndTheDownstreamIsUnreachable_ReportsUnhealthyWithoutLatching()
    {
        var attempts = 0;
        using var handler = new StubHandler(_ => ++attempts == 1
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        var duringOutage = await check.CheckHealthAsync(context, CancellationToken.None);
        var afterRecovery = await check.CheckHealthAsync(context, CancellationToken.None);

        duringOutage.Status.Should().Be(HealthStatus.Unhealthy);
        afterRecovery.Status.Should().Be(HealthStatus.Healthy);
        // Two attempts, both HTTP/2: a connectivity failure is not a protocol refusal, so the first
        // check neither fell back nor latched, and the second still had the question open.
        handler.Attempts.Should().Equal(Http2Attempt, Http2Attempt);
    }

    [Fact]
    public async Task Probe_WhenAutoFallbackAlsoFails_ReportsUnhealthyWithoutLatching()
    {
        using var handler = new StubHandler(
            _ => throw new HttpRequestException(HttpRequestError.VersionNegotiationError, "refused"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        var first = await check.CheckHealthAsync(context, CancellationToken.None);
        var second = await check.CheckHealthAsync(context, CancellationToken.None);

        first.Status.Should().Be(HealthStatus.Unhealthy);
        second.Status.Should().Be(HealthStatus.Unhealthy);
        handler.Attempts.Should().Equal(Http2Attempt, Http11Attempt, Http2Attempt, Http11Attempt);
    }

    [Fact]
    public async Task Probe_WhenPinnedToHttp2_NeverFallsBack()
    {
        using var handler = new StubHandler(RefusesHttp2(HttpRequestError.VersionNegotiationError));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Http2);

        var result = await check.CheckHealthAsync(context, CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        handler.Attempts.Should().Equal(Http2Attempt);
    }

    [Fact]
    public async Task Probe_WhenPinnedToHttp11_SendsHttp11()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };
        var (check, context) = CheckFor(client, DownstreamProbeVersion.Http11);

        var result = await check.CheckHealthAsync(context, CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Healthy);
        handler.Attempts.Should().Equal(Http11Attempt);
    }

    // Stands in for a downstream that accepts the connection and refuses the VERSION: HTTP/2 throws
    // the protocol error, HTTP/1.1 answers normally.
    private static Func<HttpRequestMessage, HttpResponseMessage> RefusesHttp2(HttpRequestError error) =>
        request => request.Version == HttpVersion.Version20
            ? throw new HttpRequestException(error, "HTTP_1_1_REQUIRED")
            : new HttpResponseMessage(HttpStatusCode.OK);

    private static (DownstreamServiceHealthCheck Check, HealthCheckContext Context) CheckFor(
        HttpClient client,
        DownstreamProbeVersion probeVersion) =>
        (new DownstreamServiceHealthCheck(new StubHttpClientFactory(client), "catalog", "client", probeVersion),
            new HealthCheckContext { Registration = RegistrationFor("catalog") });

    private static async Task<HealthCheckResult> ProbeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        using var handler = new StubHandler(respond);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://catalog") };

        var (check, context) = CheckFor(client, DownstreamProbeVersion.Auto);

        return await check.CheckHealthAsync(context, CancellationToken.None);
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

    /// <summary>One request the check actually put on the wire, with the version profile it asked for.</summary>
    /// <param name="Version">The HTTP version on the request.</param>
    /// <param name="Policy">The version policy on the request.</param>
    private sealed record ProbeAttempt(Version Version, HttpVersionPolicy Policy);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<ProbeAttempt> _attempts = [];

        // Recorded in order and BEFORE respond runs, so an attempt that throws still counts. This is
        // how a negotiation (HTTP/2 then HTTP/1.1) is told apart from a latched, single-request poll.
        public IReadOnlyList<ProbeAttempt> Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _attempts.Add(new ProbeAttempt(request.Version, request.VersionPolicy));
            return Task.FromResult(respond(request));
        }
    }
}
