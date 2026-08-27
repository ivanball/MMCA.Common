using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMCA.Common.Gateway.Transforms;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace MMCA.Common.Gateway.Tests.Transforms;

/// <summary>
/// Unit tests for the route/cluster trace headers: that they reach the proxied request, that a
/// client cannot forge them, and that a host can rename or switch them off.
/// </summary>
public sealed class GatewayTraceHeaderTransformProviderTests
{
    [Fact]
    public async Task StampsTheRouteAndClusterIdsOnTheProxiedRequest()
    {
        using var proxyRequest = await StampAsync(new GatewaySettings());

        Header(proxyRequest, GatewayTraceHeaderSettings.DefaultRouteHeaderName).Should().Be("identity-auth");
        Header(proxyRequest, GatewayTraceHeaderSettings.DefaultClusterHeaderName).Should().Be("identity");
    }

    // A header a downstream service trusts must be one only the gateway can set, so whatever the
    // client sent under the same name is dropped first rather than appended to.
    [Fact]
    public async Task ReplacesAClientSuppliedValueRatherThanAppendingToIt()
    {
        using var proxyRequest = await StampAsync(
            new GatewaySettings(),
            seed: request =>
            {
                request.Headers.TryAddWithoutValidation(GatewayTraceHeaderSettings.DefaultRouteHeaderName, "spoofed");
                request.Headers.TryAddWithoutValidation(GatewayTraceHeaderSettings.DefaultClusterHeaderName, "spoofed");
            });

        proxyRequest.Headers.GetValues(GatewayTraceHeaderSettings.DefaultRouteHeaderName)
            .Should().ContainSingle().Which.Should().Be("identity-auth");
        proxyRequest.Headers.GetValues(GatewayTraceHeaderSettings.DefaultClusterHeaderName)
            .Should().ContainSingle().Which.Should().Be("identity");
    }

    [Fact]
    public async Task HonorsCustomHeaderNames()
    {
        var settings = new GatewaySettings
        {
            TraceHeaders = new GatewayTraceHeaderSettings
            {
                RouteHeaderName = "X-Edge-Route",
                ClusterHeaderName = "X-Edge-Cluster",
            },
        };

        using var proxyRequest = await StampAsync(settings);

        Header(proxyRequest, "X-Edge-Route").Should().Be("identity-auth");
        Header(proxyRequest, "X-Edge-Cluster").Should().Be("identity");
        proxyRequest.Headers.Contains(GatewayTraceHeaderSettings.DefaultRouteHeaderName).Should().BeFalse();
    }

    [Fact]
    public void AddsNoTransformWhenDisabled()
    {
        var settings = new GatewaySettings
        {
            TraceHeaders = new GatewayTraceHeaderSettings { Enabled = false },
        };

        var context = BuilderContext();
        new GatewayTraceHeaderTransformProvider(Options.Create(settings)).Apply(context);

        context.RequestTransforms.Should().BeEmpty();
    }

    // A route with no cluster (a direct-response route, or one whose cluster failed to resolve)
    // still gets its own id stamped; the cluster header is simply absent.
    [Fact]
    public async Task StampsTheRouteIdEvenWhenTheRouteHasNoCluster()
    {
        using var proxyRequest = await StampAsync(
            new GatewaySettings(),
            route: new RouteConfig { RouteId = "static-privacy" });

        Header(proxyRequest, GatewayTraceHeaderSettings.DefaultRouteHeaderName).Should().Be("static-privacy");
        proxyRequest.Headers.Contains(GatewayTraceHeaderSettings.DefaultClusterHeaderName).Should().BeFalse();
    }

    private static async Task<HttpRequestMessage> StampAsync(
        GatewaySettings settings,
        Action<HttpRequestMessage>? seed = null,
        RouteConfig? route = null)
    {
        var context = BuilderContext(route);
        new GatewayTraceHeaderTransformProvider(Options.Create(settings)).Apply(context);

        context.RequestTransforms.Should().ContainSingle();

        var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "http://identity/Auth/login");
        seed?.Invoke(proxyRequest);

        await context.RequestTransforms[0].ApplyAsync(new RequestTransformContext
        {
            HttpContext = new DefaultHttpContext(),
            ProxyRequest = proxyRequest,
            Path = "/Auth/login",
            DestinationPrefix = "http://identity",
            HeadersCopied = true,
        });

        return proxyRequest;
    }

    private static TransformBuilderContext BuilderContext(RouteConfig? route = null) => new()
    {
        Route = route ?? new RouteConfig { RouteId = "identity-auth", ClusterId = "identity" },
        Services = new EmptyServiceProvider(),
    };

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
