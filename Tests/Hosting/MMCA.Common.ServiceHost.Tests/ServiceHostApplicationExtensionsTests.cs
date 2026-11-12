using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using MMCA.Common.ServiceHost.Tests.Support;

namespace MMCA.Common.ServiceHost.Tests;

/// <summary>
/// <c>UseMmcaServiceHostAsync</c> (design item 19, rules 19.20 to 19.24), in the Development
/// environment with <c>MmcaOpenApiDesignTime=true</c>, so database initialization is skipped exactly
/// as it is for build-time OpenAPI generation and no database is needed. The seeded
/// <c>DatabaseInitStrategy</c> is invalid on purpose: a run that reached initialization would throw
/// at once, which is how the skip, and the ordering of initialization before mapping, are observed.
/// <para>
/// Not here (CI only, design 19(c)): the non-Development case, which cannot skip initialization and
/// so needs SQL Server. It belongs to the consumers' per-service integration tiers.
/// </para>
/// </summary>
public sealed class ServiceHostApplicationExtensionsTests
{
    // Rule 19.20
    [Fact]
    public async Task UseMmcaServiceHostAsync_NullHost_ThrowsArgumentNullException()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();

        var act = () => app.UseMmcaServiceHostAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // Rule 19.21
    [Fact]
    public async Task DesignTimeDevelopment_SkipsDatabaseInitialization_WithoutADatabase()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();

        var act = () => app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("the design-time Development run skips the schema step");
    }

    // Rule 19.21: initialization is awaited first, so a failing initialization maps nothing.
    [Fact]
    public async Task InitializationFailure_PropagatesBeforeAnythingIsMapped()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder(designTime: null);
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();

        var act = () => app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Valid values are: Migrate, None*");
        RoutePatterns(app).Should().BeEmpty("the schema step runs before any endpoint is mapped");
    }

    // Rule 19.22
    [Fact]
    public async Task MapsDefaultEndpoints_AndOpenApi()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();

        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);

        var patterns = RoutePatterns(app);
        patterns.Should().Contain("/alive");
        patterns.Should().Contain("/health");
        patterns.Should().Contain(p => p.StartsWith("/openapi/", StringComparison.Ordinal));
    }

    // Rule 19.23
    [Fact]
    public async Task Development_MapsGrpcReflection()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();

        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);

        RoutePatterns(app).Should().Contain(p => p.Contains("ServerReflection", StringComparison.Ordinal));
    }

    // Rule 19.22: the zero-argument middleware pipeline serves the mapped probe on a started host.
    [Fact]
    public async Task StartedHost_AnswersAlive_ThroughTheMiddlewarePipeline()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();
        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // Rule 19.24: the factory outlives startup wiring.
    [Fact]
    public async Task BootstrapLoggerFactory_IsNotDisposedBeforeTheHostStops()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();
        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);

        Action act = () => host.BootstrapLoggerFactory.CreateLogger("x");

        act.Should().NotThrow("the ModuleLoader keeps the logger it was given for the life of the host");

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // Rule 19.24
    [Fact]
    public async Task ApplicationStopped_DisposesBootstrapLoggerFactory()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();
        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);

        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);

        Action act = () => host.BootstrapLoggerFactory.CreateLogger("x");

        act.Should().Throw<ObjectDisposedException>("ApplicationStopped disposes the bootstrap logger factory");
    }

    private static List<string> RoutePatterns(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)];
}
