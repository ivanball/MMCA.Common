using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Web.SameOriginProxy;
using Moq;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>
/// The opt-in boundary: a host that does not call <c>AddCommonSameOriginApiProxy</c> serves no proxy
/// route, the same <c>/client-config</c> document and the same Lax cookies as before; an opted-in host
/// adds the proxy base to <c>/client-config</c>, swaps the Server circuit to the protected handoffs,
/// validates its settings at startup and fails the boot when a later registration undoes the swap.
/// </summary>
public sealed class SameOriginApiProxyOptInTests : IAsyncLifetime
{
    private FakeGateway _gateway = null!;

    public async ValueTask InitializeAsync() => _gateway = await FakeGateway.StartAsync();

    public async ValueTask DisposeAsync() => await _gateway.DisposeAsync();

    [Fact]
    public async Task NotOptedIn_HasNoProxyRoute_ServesTheUnchangedClientConfig_AndKeepsLaxCookies()
    {
        using var host = await ProxyHost.StartAsync(_gateway, optIn: false);
        using var client = host.GetTestClient();

        using var proxied = await client.GetAsync(new Uri("/api/orders", UriKind.Relative), TestContext.Current.CancellationToken);
        var config = await client.GetStringAsync(new Uri("/client-config", UriKind.Relative), TestContext.Current.CancellationToken);
        using var cookieWrite = await client.PostAsync(
            new Uri("/auth/session-cookie", UriKind.Relative),
            new StringContent("""{"accessToken":"a","refreshToken":"r"}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        proxied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _gateway.Seen.Should().BeEmpty();
        config.Should().Be("{\"api\":{\"apiEndpoint\":\"" + ProxyHost.WasmApiEndpoint + "\"}}");
        ProxyHost.SetCookies(cookieWrite).Should().HaveCount(2).And.OnlyContain(c => c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
        host.Services.GetRequiredService<IOptions<SessionCookieSettings>>().Value.ClaimsOnlyBrowserTokens.Should().BeFalse();
    }

    [Fact]
    public async Task OptedIn_ClientConfigAddsTheSameOriginBase_AndKeepsTheGatewayForNavigations()
    {
        using var host = await ProxyHost.StartAsync(_gateway);
        using var client = host.GetTestClient();

        var config = await client.GetStringAsync(new Uri("/client-config", UriKind.Relative), TestContext.Current.CancellationToken);

        config.Should().Be("{\"api\":{\"apiEndpoint\":\"" + ProxyHost.WasmApiEndpoint + "\",\"sameOriginApiEndpoint\":\"/api/\"}}");
    }

    [Fact]
    public async Task OptedIn_GatewayDefaultsToApiApiEndpoint_AndAPrefixMovesTheRoute()
    {
        using var host = await ProxyHost.StartAsync(_gateway, configuration: new Dictionary<string, string?> { ["SameOriginApiProxy:PathPrefix"] = "/bff" });
        using var client = host.GetTestClient();

        using var response = await client.GetAsync(new Uri("/bff/orders", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _gateway.Seen.Should().ContainSingle().Which.PathAndQuery.Should().Be("/orders");
        host.Services.GetRequiredService<IOptions<SameOriginApiProxySettings>>().Value.GatewayAddress.Should().Be("http://gateway");
    }

    [Theory]
    [InlineData("SameOriginApiProxy:PathPrefix", "api/")]
    [InlineData("SameOriginApiProxy:PathPrefix", "/api/{id}")]
    [InlineData("SameOriginApiProxy:SessionCookieSameSite", "None")]
    [InlineData("Api:ApiEndpoint", "")]
    public async Task OptedIn_InvalidSettings_FailTheBoot(string key, string value)
    {
        var act = () => ProxyHost.StartAsync(_gateway, configuration: new Dictionary<string, string?> { [key] = value });

        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    [Fact]
    public async Task OptedIn_ReplacesTheServerCircuitTokenServices_WithTheProtectedHandoffs()
    {
        using var host = await ProxyHost.StartAsync(
            _gateway,
            beforeOptIn: services =>
            {
                services.AddScoped(_ => new Mock<IJSRuntime>().Object);
                services.AddScoped<ITokenRefresher, SameOriginProxyTokenRefresher>();
                services.AddScoped<ISessionCookieSync, JsFetchSessionCookieSync>();
            });

        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITokenRefresher>().Should().BeOfType<HandoffTokenRefresher>();
        scope.ServiceProvider.GetRequiredService<ISessionCookieSync>().Should().BeOfType<HandoffSessionCookieSync>();
    }

    [Fact]
    public async Task OptedIn_ARefresherRegisteredAfterwards_FailsTheBoot()
    {
        var act = () => ProxyHost.StartAsync(
            _gateway,
            beforeOptIn: services => services.AddScoped(_ => new Mock<IJSRuntime>().Object),
            afterOptIn: services => services.AddScoped<ITokenRefresher, SameOriginProxyTokenRefresher>());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*AFTER*");
    }
}
