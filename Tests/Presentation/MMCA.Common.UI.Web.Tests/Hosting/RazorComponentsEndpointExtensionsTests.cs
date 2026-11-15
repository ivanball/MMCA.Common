using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Web.Hosting;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// HEAD on a Blazor page route (Store manual test Run 4, O9): on .NET 10 a Razor component endpoint
/// names its methods (GET and POST) without HEAD, so a probe's <c>HEAD /</c> was a 404; .NET 11 already
/// lists HEAD. <see cref="RazorComponentsEndpointExtensions.AllowHead"/> adds HEAD to whatever the page
/// endpoint accepts, keeping the methods it already had, and changes nothing where HEAD is listed.
/// </summary>
public sealed class RazorComponentsEndpointExtensionsTests
{
    [Fact]
    public async Task WithoutAllowHead_ThePageEndpoint_AcceptsHeadOnlyFromNet11()
    {
        await using var app = BuildApp(allowHead: false);

        var methods = PageEndpointMethods(app);

        methods.Should().Contain([HttpMethods.Get, HttpMethods.Post]);
#if NET11_0_OR_GREATER
        methods.Should().Contain(HttpMethods.Head, "the .NET 11 page endpoint already lists HEAD");
#else
        methods.Should().NotContain(HttpMethods.Head, "this is the 404 the convention exists to fix");
#endif
    }

    [Fact]
    public async Task AllowHead_AddsHead_AndKeepsTheMethodsThePageAlreadyAccepted()
    {
        await using var withoutHead = BuildApp(allowHead: false);
        var original = PageEndpointMethods(withoutHead);

        await using var app = BuildApp(allowHead: true);
        var methods = PageEndpointMethods(app);

        methods.Should().Contain(HttpMethods.Head);
        methods.Should().Contain(original, "a page that handles a form post must keep POST");
        methods.Should().OnlyHaveUniqueItems("HEAD is added once, never beside one already listed");
        var expectedCount = original.Contains(HttpMethods.Head, StringComparer.OrdinalIgnoreCase) ? original.Count : original.Count + 1;
        methods.Should().HaveCount(expectedCount);
    }

    private static WebApplication BuildApp(bool allowHead)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.Services.AddRazorComponents();
        var app = builder.Build();

        var conventions = app.MapRazorComponents<ProbeRoot>();
        if (allowHead)
        {
            conventions.AllowHead();
        }

        return app;
    }

    private static IReadOnlyList<string> PageEndpointMethods(WebApplication app)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type == typeof(ProbePage));

        return endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods;
    }

    /// <summary>The root component MapRazorComponents is given; pages are discovered in its assembly.</summary>
    public sealed class ProbeRoot : ComponentBase;

    /// <summary>A routable page, discovered by its <see cref="RouteAttribute"/>.</summary>
    [Route("/allow-head-probe")]
    public sealed class ProbePage : ComponentBase;
}
