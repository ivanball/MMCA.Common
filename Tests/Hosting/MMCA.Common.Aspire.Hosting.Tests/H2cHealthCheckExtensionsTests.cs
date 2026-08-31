using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for <c>WithH2cHealthCheck</c>: what it registers on the AppHost (key, failure status,
/// probe budget) and how it associates that check with the resource, that a repeated call does not
/// produce the duplicate health-check key the health-check service rejects at startup, and how the
/// probe itself maps an HTTP/2 outcome onto a health status.
/// </summary>
public sealed class H2cHealthCheckExtensionsTests
{
    [Fact]
    public void Defaults_ProbeLivenessOnTheCleartextEndpoint()
    {
        H2cHealthCheckExtensions.DefaultProbePath.Should().Be("/alive",
            because: "a startup WaitFor gate must probe liveness: a readiness endpoint aggregates downstream "
                + "and warmup checks, so gating startup on it can deadlock the dependency graph when the "
                + "warmup path runs back through the resource that is waiting");
        H2cHealthCheckExtensions.DefaultEndpointName.Should().Be("http",
            because: "the cleartext endpoint is the h2c one; https negotiates its version through ALPN");
    }

    [Fact]
    public void WithH2cHealthCheck_AssociatesTheCheckWithTheResource()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = AddService(builder);

        service.WithH2cHealthCheck();

        HealthCheckAnnotations(service).Single().Key.Should().Be("identity-h2c-http");
    }

    [Fact]
    public void WithH2cHealthCheck_RegistersTheProbeInTheAppHostContainer()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        AddService(builder).WithH2cHealthCheck();

        var registration = H2cRegistrations(builder).Single();

        registration.Name.Should().Be("identity-h2c-http");
        registration.FailureStatus.Should().Be(HealthStatus.Unhealthy);
        registration.Timeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    // A duplicate health-check key is a startup exception, not a harmless second registration, so a
    // host and a helper both asking to gate the same endpoint must not compound.
    [Fact]
    public void WithH2cHealthCheck_IsIdempotent()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = AddService(builder);

        var first = service.WithH2cHealthCheck();
        var second = service.WithH2cHealthCheck();

        second.Should().BeSameAs(first);
        HealthCheckAnnotations(service).Should().ContainSingle();
        H2cRegistrations(builder).Should().ContainSingle();
    }

    [Fact]
    public void WithH2cHealthCheck_KeysTheCheckByEndpointSoOneResourceCanGateTwo()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = AddService(builder);

        service.WithH2cHealthCheck();
        service.WithH2cHealthCheck(endpointName: "grpc");

        H2cRegistrations(builder).Select(r => r.Name)
            .Should().BeEquivalentTo("identity-h2c-http", "identity-h2c-grpc");
    }

    // The duplicate guard hangs off the app builder's own service collection rather than a static
    // field. A test run creates many builders in one process, and a static ledger would make the
    // second builder's registration silently disappear.
    [Fact]
    public void WithH2cHealthCheck_DoesNotShareItsDuplicateGuardAcrossBuilders()
    {
        var first = DistributedApplication.CreateBuilder([]);
        AddService(first).WithH2cHealthCheck();

        var second = DistributedApplication.CreateBuilder([]);
        AddService(second).WithH2cHealthCheck();

        H2cRegistrations(first).Should().ContainSingle();
        H2cRegistrations(second).Should().ContainSingle();
    }

    // GetEndpoint resolves lazily, so naming an endpoint the resource never declares is NOT an error
    // at registration time. It surfaces at probe time as a permanently unhealthy check (the endpoint
    // never allocates, see the unallocated probe test below), which keeps the dependent resource
    // waiting rather than letting it start against something nobody verified.
    [Fact]
    public void WithH2cHealthCheck_WithAnUndeclaredEndpointName_RegistersLazilyRatherThanThrowing()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = AddService(builder);

        var act = () => service.WithH2cHealthCheck(endpointName: "does-not-exist");

        act.Should().NotThrow();
        H2cRegistrations(builder).Single().Name.Should().Be("identity-h2c-does-not-exist");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithH2cHealthCheck_RejectsABlankPath(string path)
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = AddService(builder);

        var act = () => service.WithH2cHealthCheck(path);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithH2cHealthCheck_RejectsABlankEndpointName(string endpointName)
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var service = AddService(builder);

        var act = () => service.WithH2cHealthCheck(endpointName: endpointName);

        act.Should().Throw<ArgumentException>();
    }

    // The whole reason this extension exists: an HttpClient on its own defaults sends HTTP/1.1, which
    // an Http2-only cleartext endpoint answers with GOAWAY HTTP_1_1_REQUIRED, so the stock probe can
    // never turn such a resource healthy.
    [Fact]
    public async Task Probe_SendsHttp2WithPriorKnowledge()
    {
        Version? version = null;
        HttpVersionPolicy? versionPolicy = null;

        await ProbeAsync(request =>
        {
            version = request.Version;
            versionPolicy = request.VersionPolicy;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        version.Should().Be(HttpVersion.Version20);
        versionPolicy.Should().Be(HttpVersionPolicy.RequestVersionExact,
            because: "h2c prior knowledge means the request must go out as HTTP/2, never negotiate down");
    }

    [Fact]
    public async Task Probe_TargetsTheConfiguredPathOnTheEndpoint()
    {
        Uri? requested = null;

        await ProbeAsync(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        requested.Should().Be(new Uri("http://identity:8080/health/ready"));
    }

    [Fact]
    public async Task Probe_WhenTheServiceAnswersSuccess_ReportsHealthy()
    {
        var result = await ProbeAsync(_ => new HttpResponseMessage(HttpStatusCode.OK));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Probe_WhenTheServiceAnswersNonSuccess_ReportsUnhealthy()
    {
        var result = await ProbeAsync(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("503");
    }

    [Fact]
    public async Task Probe_WhenTheServiceIsUnreachable_ReportsUnhealthyWithTheException()
    {
        var result = await ProbeAsync(_ => throw new HttpRequestException("connection refused"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Probe_WhenTheProbeTimesOut_ReportsUnhealthy()
    {
        var result = await ProbeAsync(_ => throw new TaskCanceledException("probe budget exhausted"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    // The registration's own timeout cancels the SAME token CheckHealthAsync receives. A probe that
    // outlives ProbeTimeout therefore arrives here with a cancelled token, and it must still come
    // back as an Unhealthy RESULT: letting it escape gets it logged by the health service as an
    // unhandled exception on every poll (the 2026-08-31 apphost-smoke logs were full of exactly
    // that noise).
    [Fact]
    public async Task Probe_WhenTheRegistrationTimeoutHasCancelledTheToken_StillReportsUnhealthyRatherThanThrowing()
    {
        using var handler = new StubHandler(_ => throw new TaskCanceledException("probe budget exhausted"));
        using var client = new HttpClient(handler, disposeHandler: false);
        var check = new H2cEndpointHealthCheck(() => "http://identity:8080", "/alive", client.SendAsync);
        var registration = new HealthCheckRegistration("identity-h2c-http", check, HealthStatus.Unhealthy, tags: null);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration },
            cancelled.Token);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<TaskCanceledException>();
    }

    // Pins the poisoned-connection guard on the shared probe handler. HTTP/2 pools one connection
    // per origin, and the Aspire endpoint proxy listens before the target Kestrel does, so the first
    // probe of a starting resource can open a connection that is accepted and never answered. The
    // keep-alive ping trio is what tears that zombie down so a later probe can open a fresh
    // connection; without it the shared client queues every probe onto the zombie forever and the
    // resource can never turn healthy (the 2026-08-31 apphost-smoke wedge). Verified empirically
    // against a held-open unanswered socket; this test pins the settings so a refactor cannot
    // silently drop them.
    [Fact]
    public void ProbeHandler_KeepsTheKeepAlivePingGuardAgainstPoisonedConnections()
    {
        var handler = H2cEndpointHealthCheck.ProbeHandler;

        handler.KeepAlivePingPolicy.Should().Be(
            HttpKeepAlivePingPolicy.Always,
            because: "a zombie connection carries no active streams, so a WithActiveRequests policy would never ping it down");
        handler.KeepAlivePingDelay.Should().Be(TimeSpan.FromSeconds(1));
        handler.KeepAlivePingTimeout.Should().Be(TimeSpan.FromSeconds(1));
        handler.UseProxy.Should().BeFalse();
    }

    // Aspire allocates an endpoint only once the resource starts, so the first polls after the
    // AppHost comes up resolve nothing. Failing them is correct: Aspire releases a WaitFor edge only
    // on Healthy, so a pre-allocation poll that passed would defeat the gate entirely.
    [Fact]
    public async Task Probe_WhenTheEndpointIsNotAllocatedYet_ReportsUnhealthyWithoutThrowing()
    {
        var result = await ProbeAsync(
            _ => new HttpResponseMessage(HttpStatusCode.OK),
            () => throw new InvalidOperationException("The endpoint is not allocated."),
            "/health/ready");

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    // AddResource rather than AddProject: the extension only needs a ProjectResource in the model,
    // and AddProject validates that the project file exists on disk, which no unit test should have
    // to fabricate.
    private static IResourceBuilder<ProjectResource> AddService(
        IDistributedApplicationBuilder builder,
        string name = "identity") =>
        builder.AddResource(new ProjectResource(name));

    private static IReadOnlyList<HealthCheckAnnotation> HealthCheckAnnotations(
        IResourceBuilder<ProjectResource> service) =>
        [.. service.Resource.Annotations.OfType<HealthCheckAnnotation>()];

    // Filtered to this extension's own keys: the AppHost container carries health checks of Aspire's
    // own, and this suite is not asserting anything about those.
    private static IReadOnlyList<HealthCheckRegistration> H2cRegistrations(IDistributedApplicationBuilder builder)
    {
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetService<IOptions<HealthCheckServiceOptions>>();

        return options is null
            ? []
            : [.. options.Value.Registrations.Where(r => r.Name.Contains("-h2c-", StringComparison.Ordinal))];
    }

    private static Task<HealthCheckResult> ProbeAsync(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        ProbeAsync(respond, () => "http://identity:8080", "/health/ready");

    private static async Task<HealthCheckResult> ProbeAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Func<string> resolveEndpointUrl,
        string path)
    {
        using var handler = new StubHandler(respond);
        using var client = new HttpClient(handler, disposeHandler: false);

        var check = new H2cEndpointHealthCheck(resolveEndpointUrl, path, client.SendAsync);
        var registration = new HealthCheckRegistration(
            "identity-h2c-http",
            check,
            HealthStatus.Unhealthy,
            tags: null);

        return await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration },
            CancellationToken.None);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
