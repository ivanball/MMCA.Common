using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Web.ClientConfig;
using MMCA.Common.UI.Web.Hardening;
using MMCA.Common.UI.Web.Hosting;
using MMCA.Common.UI.Web.Pwa;
using MMCA.Common.UI.Web.SameOriginProxy;
using Moq;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// Specifies <c>UseMmcaBlazorWebPipeline</c> and <c>GetMmcaRazorComponentAssemblies</c> (item 20, rules
/// 16 to 21) on a TestServer: the composed host plus the composed pipeline, with the test's own probe
/// endpoints mapped after the pipeline call where a real head maps <c>MapRazorComponents</c>.
/// </summary>
public sealed class BlazorWebPipelineTests
{
    private const string ProbePath = "/probe";
    private const string ClientIp = "203.0.113.7";

    /// <summary>
    /// Rule 20.17: the UI limiter sits directly after the security headers, ahead of the
    /// exception-handler position (where Development runs the <c>DevelopmentPipeline</c> callback) and
    /// ahead of the status-code re-execute. A throttled request therefore answers 429 with an empty body,
    /// still carries the security headers, and never reaches anything downstream; a limiter placed after
    /// that position (Store's old order) would let the second request reach the probe middleware.
    /// </summary>
    [Fact]
    public async Task ThrottledRequest_Returns429_WithoutReExecutedPage()
    {
        var builder = BlazorWebHostHarness.CreateBuilder(
            Environments.Development,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{UiRateLimitingSettings.SectionName}:Enabled"] = "true",
                [$"{UiRateLimitingSettings.SectionName}:PermitLimit"] = "1",
                [$"{UiRateLimitingSettings.SectionName}:WindowSeconds"] = "60",
            });
        // DESIGN-QUESTION: 20(c) asks only for "429 with an empty body", which a limiter placed AFTER the
        // re-execute also produces (the re-executed /not-found is throttled too). The discriminating
        // assertion is the probe in the DevelopmentPipeline slot, which is the exception-handler position.
        var reachedExceptionHandlerPosition = 0;
        var notFoundPageRenders = 0;
        var pipeline = new MmcaBlazorWebPipelineOptions
        {
            DevelopmentPipeline = branch => branch.Use(async (httpContext, next) =>
            {
                Interlocked.Increment(ref reachedExceptionHandlerPosition);
                await next(httpContext);
            }),
        };

        await using var app = await BlazorWebHostHarness.StartAsync(
            builder,
            pipelineOptions: pipeline,
            afterPipeline: web =>
            {
                web.MapGet(ProbePath, () => "ok").AllowAnonymous();
                web.MapGet("/not-found", () =>
                {
                    Interlocked.Increment(ref notFoundPageRenders);
                    return "not found page";
                }).AllowAnonymous();
            });
        var server = app.GetTestServer();

        var first = await SendFromAsync(server, ProbePath);
        var throttled = await SendFromAsync(server, ProbePath);

        first.StatusCode.Should().Be(StatusCodes.Status200OK);
        throttled.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        throttled.Body.Should().BeEmpty("the limiter writes no body and nothing downstream re-renders one");
        throttled.Headers.XContentTypeOptions.ToString().Should().Be("nosniff", "the security headers run before the limiter");
        reachedExceptionHandlerPosition.Should().Be(1, "only the admitted request may reach the exception-handler position");
        notFoundPageRenders.Should().Be(0);
    }

    /// <summary>Rule 20.16: the security headers run first, so even an unmatched request carries them.</summary>
    [Fact]
    public async Task SecurityHeaders_PresentOnA404()
    {
        await using var app = await BlazorWebHostHarness.StartAsync(BlazorWebHostHarness.CreateBuilder());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/no-such-page", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.Contains("Content-Security-Policy").Should().BeTrue("the dynamic Blazor CSP is emitted on every response");
    }

    /// <summary>Rule 20.20: the client-config document is mapped, and the extras callback reaches it.</summary>
    [Fact]
    public async Task ClientConfig_IsMapped_AndCarriesExtras()
    {
        var pipeline = new MmcaBlazorWebPipelineOptions
        {
            ClientConfig = config => config.Add("Support", new { Email = "help@example.com" }),
        };
        await using var app = await BlazorWebHostHarness.StartAsync(BlazorWebHostHarness.CreateBuilder(), pipelineOptions: pipeline);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(ClientConfigEndpointExtensions.ClientConfigPath, UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("api").GetProperty("apiEndpoint").GetString().Should().Be(BlazorWebHostHarness.WasmApiEndpoint);
        document.RootElement.GetProperty("support").GetProperty("email").GetString().Should().Be("help@example.com");
    }

    /// <summary>
    /// Rule 20.12 seen from the pipeline: the proxy registration comes after the token refresher and
    /// cookie sync, so <c>MapCommonSameOriginApiProxy</c> (which fails the boot otherwise) maps, and the
    /// circuit's refresher is the protected-handoff one.
    /// </summary>
    [Fact]
    public async Task SameOriginProxy_MapsWithoutBootFailure()
    {
        await using var app = await BlazorWebHostHarness.StartAsync(BlazorWebHostHarness.CreateBuilder());

        RoutePatterns(app).Should().Contain(pattern => pattern.StartsWith("/api/", StringComparison.Ordinal));
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITokenRefresher>().Should().BeOfType<HandoffTokenRefresher>();
    }

    /// <summary>Rule 20.20: the installable-web-app worker is served at the site root.</summary>
    [Fact]
    public async Task ServiceWorker_IsServed()
    {
        await using var app = await BlazorWebHostHarness.StartAsync(BlazorWebHostHarness.CreateBuilder());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            new Uri(ServiceWorkerEndpointExtensions.ServiceWorkerPath, UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
    }

    /// <summary>Rule 20.20: the remaining framework endpoints the heads map today are all mapped.</summary>
    [Fact]
    public async Task SessionCookieAndCultureEndpoints_AreMapped()
    {
        await using var app = await BlazorWebHostHarness.StartAsync(BlazorWebHostHarness.CreateBuilder());

        var patterns = RoutePatterns(app);
        patterns.Should().Contain("/auth/session/token");
        patterns.Should().Contain("/culture/set");
        patterns.Should().Contain(ClientConfigEndpointExtensions.ClientConfigPath);
        patterns.Should().Contain(ServiceWorkerEndpointExtensions.ServiceWorkerPath);
    }

    /// <summary>
    /// Rule 20.18: the development callback is the Development branch, and nothing else: invoked once
    /// while the pipeline is built in Development, never outside it.
    /// </summary>
    [Theory]
    [InlineData("Development", 1)]
    [InlineData("Production", 0)]
    [InlineData("Staging", 0)]
    public async Task DevelopmentPipeline_RunsOnlyInDevelopment(string environmentName, int expectedCalls)
    {
        var calls = 0;
        var pipeline = new MmcaBlazorWebPipelineOptions { DevelopmentPipeline = _ => calls++ };

        await using var app = await BlazorWebHostHarness.StartAsync(
            BlazorWebHostHarness.CreateBuilder(environmentName),
            pipelineOptions: pipeline);

        calls.Should().Be(expectedCalls);
    }

    /// <summary>
    /// Rule 20.18, the other branch: outside Development an unhandled exception re-executes
    /// <c>/Error</c> (the shared SSR error page a real head serves; mapped here by the test).
    /// </summary>
    [Fact]
    public async Task OutsideDevelopment_AnUnhandledException_RendersTheErrorPage()
    {
        await using var app = await BlazorWebHostHarness.StartAsync(
            BlazorWebHostHarness.CreateBuilder(),
            afterPipeline: web =>
            {
                web.MapGet("/boom", string () => throw new InvalidOperationException("boom")).AllowAnonymous();
                web.MapGet("/Error", () => "error page").AllowAnonymous();
            });
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/boom", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("error page");
    }

    /// <summary>
    /// Rule 20.21: the two framework assemblies, then the client assembly, then every registered UI
    /// module's assembly, first occurrence kept (ADC's list today).
    /// </summary>
    [Fact]
    public async Task GetMmcaRazorComponentAssemblies_OrdersAndDeduplicates()
    {
        var commonUi = typeof(MMCA.Common.UI._Imports).Assembly;
        var commonUiWeb = typeof(MMCA.Common.UI.Web._Imports).Assembly;
        var client = typeof(BlazorWebPipelineTests).Assembly;
        var moduleOnly = typeof(MudBlazor.MudButton).Assembly;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddSingleton(UiModule(commonUi));
        builder.Services.AddSingleton(UiModule(moduleOnly));
        builder.Services.AddSingleton(UiModule(client));
        builder.Services.AddSingleton(UiModule(moduleOnly));
        await using var app = builder.Build();

        var assemblies = app.GetMmcaRazorComponentAssemblies(client);

        assemblies.Should().Equal(commonUi, commonUiWeb, client, moduleOnly);
    }

    /// <summary>Rule 20.21: a null client assembly is refused.</summary>
    [Fact]
    public async Task GetMmcaRazorComponentAssemblies_NullClientAssembly_Throws()
    {
        await using var app = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production }).Build();

        var act = () => app.GetMmcaRazorComponentAssemblies(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static IUIModule UiModule(System.Reflection.Assembly assembly)
    {
        var module = new Mock<IUIModule>();
        module.SetupGet(m => m.Assembly).Returns(assembly);
        module.SetupGet(m => m.NavItems).Returns([]);
        return module.Object;
    }

    private static List<string> RoutePatterns(WebApplication app) =>
    [
        .. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty),
    ];

    /// <summary>
    /// Sends a GET from a fixed client address. TestServer leaves the remote address null and the UI
    /// limiter fails open on an unattributable caller, so the address is set on the context directly.
    /// </summary>
    private static async Task<(int StatusCode, string Body, IHeaderDictionary Headers)> SendFromAsync(TestServer server, string path)
    {
        var context = await server.SendAsync(
            request =>
            {
                request.Request.Method = HttpMethods.Get;
                request.Request.Path = path;
                request.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
            },
            TestContext.Current.CancellationToken);

        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, body, context.Response.Headers);
    }
}
