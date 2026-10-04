using System.Net;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// Drives the downstream checks the way a gateway's readiness endpoint does: through the REAL
/// <c>AddGatewayDownstreamHealthChecks</c> registration and <see cref="HealthCheckService"/>, one
/// <c>CheckHealthAsync</c> per poll. The unit tests in <see cref="GatewayDownstreamHealthChecksTests"/>
/// call one hand-built check instance twice, so they cannot see what the health-check service does
/// between polls (build the check again from its registration factory), nor what the HttpClient
/// pipeline <c>AddServiceDefaults()</c> wraps around the probe client.
/// <para>
/// The downstream here is a cleartext Http1AndHttp2 head without ALPN: it refuses HTTP/2 with a
/// protocol error and answers HTTP/1.1. The negotiation must happen once per process, and a refused
/// attempt must reach the check at once so it can fall back inside the two-second probe budget.
/// </para>
/// </summary>
public sealed class GatewayDownstreamHealthCheckPollingTests
{
    private const string ServiceName = "catalog";

    private static readonly ProbeAttempt Http2Attempt =
        new(HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact);

    private static readonly ProbeAttempt Http11Attempt =
        new(HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower);

    [Fact]
    public async Task ReadinessPolls_ThroughTheRegisteredCheck_ReuseTheVersionLatchedOnTheFirstPoll()
    {
        var recorder = new AttemptRecorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGatewayDownstreamHealthChecks(ServiceName);
        services.AddHttpClient(GatewayHealthCheckExtensions.ClientName(ServiceName))
            .ConfigurePrimaryHttpMessageHandler(() => new RefusesHttp2Handler(recorder));
        await using var provider = services.BuildServiceProvider();
        var healthChecks = provider.GetRequiredService<HealthCheckService>();

        var first = await PollAsync(healthChecks);
        var firstAttempts = recorder.TakeAll();
        var second = await PollAsync(healthChecks);
        var secondAttempts = recorder.TakeAll();

        using (new AssertionScope())
        {
            first.Status.Should().Be(HealthStatus.Healthy);
            firstAttempts.Should().Equal(Http2Attempt, Http11Attempt);
            second.Status.Should().Be(HealthStatus.Healthy);
            secondAttempts.Should().Equal(
                [Http11Attempt],
                "the first poll settled on HTTP/1.1, and that latch must outlive the poll: a check rebuilt per poll renegotiates (and re-sends the refused HTTP/2 attempt) every time");
        }
    }

    [Fact]
    public async Task ReadinessPolls_UnderServiceDefaults_FallBackWithoutARetryAndStayHealthyWithinTheProbeBudget()
    {
        var recorder = new AttemptRecorder();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddServiceDefaults();
        builder.Services.AddGatewayDownstreamHealthChecks(ServiceName);

        // Only the transport is replaced; the resilience and service-discovery handlers that
        // AddServiceDefaults() puts on every factory client stay in the probe pipeline.
        builder.Services.AddHttpClient(GatewayHealthCheckExtensions.ClientName(ServiceName))
            .ConfigurePrimaryHttpMessageHandler(() => new RefusesHttp2Handler(recorder));
        using var host = builder.Build();
        var healthChecks = host.Services.GetRequiredService<HealthCheckService>();

        var first = await PollAsync(healthChecks);
        var firstAttempts = recorder.TakeAll();
        var second = await PollAsync(healthChecks);
        var secondAttempts = recorder.TakeAll();

        using (new AssertionScope())
        {
            firstAttempts.Should().Equal(
                [Http2Attempt, Http11Attempt],
                "a refused HTTP/2 attempt must go straight to the HTTP/1.1 fallback: a Polly retry of the refused attempt (about two seconds of backoff) spends the whole probe budget and reports a healthy downstream as Unhealthy");
            first.Status.Should().Be(HealthStatus.Healthy);
            first.Duration.Should().BeLessThan(GatewayHealthCheckExtensions.ProbeTimeout);
            secondAttempts.Should().Equal(
                [Http11Attempt],
                "the version latched on the first poll must survive to the next one, so the second poll sends no HTTP/2 attempt");
            second.Status.Should().Be(HealthStatus.Healthy);
            second.Duration.Should().BeLessThan(GatewayHealthCheckExtensions.ProbeTimeout);
        }
    }

    private static async Task<HealthReportEntry> PollAsync(HealthCheckService healthChecks)
    {
        var checkName = GatewayHealthCheckExtensions.CheckName(ServiceName);
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Name == checkName,
            TestContext.Current.CancellationToken);
        return report.Entries[checkName];
    }

    /// <summary>One request that reached the transport, with the version profile it asked for.</summary>
    /// <param name="Version">The HTTP version on the request.</param>
    /// <param name="Policy">The version policy on the request.</param>
    private sealed record ProbeAttempt(Version Version, HttpVersionPolicy Policy);

    /// <summary>Collects the attempts that reached the transport, drained once per poll.</summary>
    private sealed class AttemptRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<ProbeAttempt> _attempts = [];

        public void Add(ProbeAttempt attempt)
        {
            lock (_gate)
            {
                _attempts.Add(attempt);
            }
        }

        public ProbeAttempt[] TakeAll()
        {
            lock (_gate)
            {
                ProbeAttempt[] taken = [.. _attempts];
                _attempts.Clear();
                return taken;
            }
        }
    }

    /// <summary>
    /// The transport of a cleartext Http1AndHttp2 downstream without ALPN: HTTP/2 is refused with
    /// the protocol error, HTTP/1.1 answers 200. Each attempt is recorded BEFORE it is answered, so a
    /// refused one still counts.
    /// </summary>
    private sealed class RefusesHttp2Handler(AttemptRecorder recorder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            recorder.Add(new ProbeAttempt(request.Version, request.VersionPolicy));

            return request.Version == HttpVersion.Version20
                ? Task.FromException<HttpResponseMessage>(
                    new HttpRequestException(HttpRequestError.VersionNegotiationError, "HTTP_1_1_REQUIRED"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
