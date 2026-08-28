using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Gateway.Tests;

/// <summary>
/// The forwarded-headers wiring a gateway host used to hand-roll. Two things are load-bearing and
/// both are asserted: the three headers that are honored, and the CLEARED known-proxy allow-lists.
/// Leaving the framework defaults in place makes the middleware ignore every forwarded header a cloud
/// ingress sends (its internal IP is in neither list), which collapses a per-client-IP rate-limit
/// partition into one shared window for every real user, silently and only in production.
/// </summary>
public sealed class ForwardedHeadersExtensionsTests
{
    [Fact]
    public void Options_HonorTheThreeForwardedHeaders() =>
        ForwardedHeadersExtensions.CreateForwardedHeadersOptions().ForwardedHeaders.Should().Be(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);

    [Fact]
    public void Options_ClearTheKnownProxyAllowLists()
    {
        var options = ForwardedHeadersExtensions.CreateForwardedHeadersOptions();

        options.KnownProxies.Should().BeEmpty(
            "an Azure Container Apps or ALB ingress front-ends from an internal IP that is in neither default list");
        options.KnownIPNetworks.Should().BeEmpty();
    }

    [Fact]
    public void Options_AreFreshPerCallSoAHostCanCustomizeWithoutLeaking() =>
        ForwardedHeadersExtensions.CreateForwardedHeadersOptions().Should().NotBeSameAs(
            ForwardedHeadersExtensions.CreateForwardedHeadersOptions());

    [Fact]
    public async Task UseCommonForwardedHeaders_RewritesTheRequestFromTheForwardedHeaders()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("ingress.internal");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "gateway.example.com";

        await BuildPipeline().Invoke(context);

        context.Connection.RemoteIpAddress?.ToString().Should().Be(
            "203.0.113.7",
            "the edge rate limiter partitions on the client IP, so it can only see the real caller once the headers have been applied");
        context.Request.Scheme.Should().Be("https");
        context.Request.Host.Value.Should().Be("gateway.example.com");
    }

    [Fact]
    public async Task UseCommonForwardedHeaders_LeavesAnUnproxiedRequestAlone()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:6001");

        await BuildPipeline().Invoke(context);

        context.Connection.RemoteIpAddress.Should().Be(System.Net.IPAddress.Loopback);
        context.Request.Scheme.Should().Be("http");
        context.Request.Host.Value.Should().Be("localhost:6001");
    }

    [Fact]
    public void UseCommonForwardedHeaders_RejectsANullBuilder()
    {
        IApplicationBuilder app = null!;

        var act = () => app.UseCommonForwardedHeaders();

        act.Should().Throw<ArgumentNullException>();
    }

    private static RequestDelegate BuildPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        var appBuilder = new ApplicationBuilder(services.BuildServiceProvider());
        appBuilder.UseCommonForwardedHeaders();
        appBuilder.Run(static _ => Task.CompletedTask);

        return appBuilder.Build();
    }
}
