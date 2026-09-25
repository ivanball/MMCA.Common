using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Startup;
using ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Tests for <see cref="ForwardedHeadersDefaults"/> and <c>UseCommonUiForwardedHeaders</c>: the
/// default mask is For, Proto and Host, both allow-lists are cleared (cloud ingress addresses are
/// not knowable ahead of time), and a UI host that calls the extension sees the forwarded scheme,
/// host and client address on the request.
/// </summary>
public sealed class ForwardedHeadersDefaultsTests
{
    [Fact]
    public void Create_ByDefault_HonorsForProtoAndHost()
    {
        var options = ForwardedHeadersDefaults.Create();

        options.ForwardedHeaders.Should().Be(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost);
        ForwardedHeadersDefaults.DefaultHeaders.Should().Be(options.ForwardedHeaders);
    }

    [Fact]
    public void Create_ClearsTheKnownProxyAndNetworkAllowLists()
    {
        var options = ForwardedHeadersDefaults.Create();

        options.KnownProxies.Should().BeEmpty();
        options.KnownIPNetworks.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithAnExplicitMask_UsesItAndStillClearsTheAllowLists()
    {
        var options = ForwardedHeadersDefaults.Create(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        options.ForwardedHeaders.Should().Be(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.KnownProxies.Should().BeEmpty();
        options.KnownIPNetworks.Should().BeEmpty();
    }

    [Fact]
    public void Create_ReturnsAFreshInstanceEachCall() =>
        ForwardedHeadersDefaults.Create().Should().NotBeSameAs(ForwardedHeadersDefaults.Create());

    [Fact]
    public async Task UseCommonUiForwardedHeaders_RewritesSchemeHostAndClientAddress()
    {
        await using var app = await StartAsync(static app => app.UseCommonUiForwardedHeaders());
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/echo", UriKind.Relative));
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "shop.example.com");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Be("https|shop.example.com|203.0.113.7");
    }

    [Fact]
    public async Task UseCommonUiForwardedHeaders_WithoutHost_LeavesTheHostAlone()
    {
        await using var app = await StartAsync(static app =>
            app.UseCommonUiForwardedHeaders(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto));
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/echo", UriKind.Relative));
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "shop.example.com");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().StartWith("https|localhost|");
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        configure(app);
        app.MapGet("/echo", static (HttpContext context) =>
            $"{context.Request.Scheme}|{context.Request.Host.Host}|{context.Connection.RemoteIpAddress?.ToString()}");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
