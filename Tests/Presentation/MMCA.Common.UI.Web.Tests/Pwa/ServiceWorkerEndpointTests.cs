using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.UI.Web.Pwa;

namespace MMCA.Common.UI.Web.Tests.Pwa;

/// <summary>
/// Tests for <c>MapCommonServiceWorker</c>: the served script (a JavaScript content type, never cached,
/// stamped with the package version), the configuration kill switch that serves the inert worker
/// instead, and the anonymous, undocumented endpoint metadata the browser's update check depends on.
/// The worker's own rule (navigations only, never caching a page) is pinned on the script text.
/// </summary>
public sealed class ServiceWorkerEndpointTests
{
    [Fact]
    public async Task ServesTheActiveWorker_AsUncachedJavaScript()
    {
        await using var app = CreateApp();
        app.MapCommonServiceWorker();

        var (context, script) = await InvokeAsync(app);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.ContentType.Should().StartWith("text/javascript");
        context.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
        script.Should().Contain("addEventListener('fetch'");
        script.Should().Contain("/_content/MMCA.Common.UI.Web/offline.html");
    }

    [Fact]
    public async Task TheActiveWorker_HandlesGetNavigationsOnly_AndNeverCachesAResponse()
    {
        await using var app = CreateApp();
        app.MapCommonServiceWorker();

        var (_, script) = await InvokeAsync(app);

        script.Should().Contain("request.method !== 'GET' || request.mode !== 'navigate'");
        script.Should().NotContain("cache.put(");
        script.Should().NotContain("cache.addAll(");
    }

    [Fact]
    public async Task TheScript_CarriesThePackageVersion_SoEachReleaseUpdatesTheWorker()
    {
        await using var app = CreateApp();
        app.MapCommonServiceWorker();

        var (_, script) = await InvokeAsync(app);

        var version = typeof(ServiceWorkerEndpointExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        script.Should().Contain($"const VERSION = '{version}';");
        script.Should().NotContain("__MMCA_SW_VERSION__");
    }

    [Fact]
    public async Task WhenDisabled_ServesTheRetiredWorker_WithNoFetchHandler()
    {
        await using var app = CreateApp((ServiceWorkerEndpointExtensions.EnabledKey, "false"));
        app.MapCommonServiceWorker();

        var (context, script) = await InvokeAsync(app);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
        script.Should().Contain("RETIRED");
        script.Should().Contain("caches.delete(");
        script.Should().NotContain("addEventListener('fetch'");
    }

    [Fact]
    public async Task TheEndpoint_IsAnonymousAndExcludedFromTheApiDescription()
    {
        await using var app = CreateApp();
        app.MapCommonServiceWorker();

        var endpoint = FindEndpoint(app);

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription.Should().BeTrue();
        endpoint.RoutePattern.RawText.Should().Be(ServiceWorkerEndpointExtensions.ServiceWorkerPath);
    }

    private static WebApplication CreateApp(params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(
            settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)));
        return builder.Build();
    }

    private static RouteEndpoint FindEndpoint(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, ServiceWorkerEndpointExtensions.ServiceWorkerPath, StringComparison.Ordinal));

    private static async Task<(HttpContext Context, string Script)> InvokeAsync(WebApplication app)
    {
        var endpoint = FindEndpoint(app);
        await using var scope = app.Services.CreateAsyncScope();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = body;

        await endpoint.RequestDelegate!(context);

        body.Position = 0;
        using var reader = new StreamReader(body);
        return (context, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
