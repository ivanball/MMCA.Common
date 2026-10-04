using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Web.Services;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// Pins <see cref="BrowserOriginHandler"/>: a server-side API call made for a visitor carries that
/// visitor's address in <c>X-Forwarded-For</c> and the browser's user-agent in <c>User-Agent</c>, each
/// as the single value, nothing is sent when no visitor request is in scope, and
/// <c>AddCommonServerTokenStorage()</c> composes the handler onto the <c>"APIClient"</c> pipeline.
/// </summary>
public sealed class BrowserOriginHandlerTests
{
    private const string HeaderName = "X-Forwarded-For";
    private const string UserAgentHeaderName = "User-Agent";
    private const string BrowserIp = "198.51.100.23";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";
    private static readonly Uri ApiUri = new("https://gateway.example.com/Auth/register");

    [Fact]
    public async Task SendAsync_WithAVisitorRequestInScope_AddsTheBrowserAddress()
    {
        var (client, inner) = CreateClient(VisitorContext(BrowserIp));

        await client.PostAsync(ApiUri, content: null, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(BrowserIp);
    }

    [Fact]
    public async Task SendAsync_WithAnIPv6VisitorAddress_ForwardsIt()
    {
        var (client, inner) = CreateClient(VisitorContext("2001:db8::7"));

        await client.GetAsync(ApiUri, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be("2001:db8::7");
    }

    [Fact]
    public async Task SendAsync_WithNoRequestInScope_SendsNoHeader()
    {
        var (client, inner) = CreateClient(httpContext: null);

        await client.GetAsync(ApiUri, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.Contains(HeaderName).Should().BeFalse();
        inner.LastRequest.Headers.Contains(UserAgentHeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenTheRequestHasNoRemoteAddress_SendsNoHeader()
    {
        var (client, inner) = CreateClient(new DefaultHttpContext());

        await client.GetAsync(ApiUri, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.Contains(HeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenTheRequestAlreadyCarriesTheHeader_ReplacesItRatherThanAppending()
    {
        var (client, inner) = CreateClient(VisitorContext(BrowserIp));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);
        request.Headers.TryAddWithoutValidation(HeaderName, "203.0.113.99, 10.0.0.1");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(BrowserIp);
    }

    [Fact]
    public async Task SendAsync_IgnoresAForwardedForHeaderTheBrowserSentToThisHost()
    {
        // The browser's own X-Forwarded-For is client-supplied: only the connection address (as this
        // host's forwarded-headers middleware resolved it) is forwarded.
        var context = VisitorContext(BrowserIp);
        context.Request.Headers[HeaderName] = "203.0.113.66";
        var (client, inner) = CreateClient(context);

        await client.GetAsync(ApiUri, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(BrowserIp);
    }

    [Fact]
    public async Task SendAsync_WithAVisitorRequestInScope_ForwardsTheBrowserUserAgent()
    {
        var (client, inner) = CreateClient(VisitorContext(BrowserIp, BrowserUserAgent));

        await client.PostAsync(ApiUri, content: null, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.UserAgent.ToString().Should().Be(BrowserUserAgent);
    }

    [Fact]
    public async Task SendAsync_WhenTheRequestAlreadyCarriesAUserAgent_ReplacesIt()
    {
        var (client, inner) = CreateClient(VisitorContext(BrowserIp, BrowserUserAgent));
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri);
        request.Headers.TryAddWithoutValidation(UserAgentHeaderName, "MMCA-Host/1.0");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.UserAgent.ToString().Should().Be(BrowserUserAgent);
    }

    [Fact]
    public async Task SendAsync_WhenTheBrowserSentNoUserAgent_SendsNone()
    {
        var (client, inner) = CreateClient(VisitorContext(BrowserIp, userAgent: "   "));

        await client.GetAsync(ApiUri, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.Contains(UserAgentHeaderName).Should().BeFalse();
        inner.LastRequest.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(BrowserIp);
    }

    [Fact]
    public async Task AddCommonServerTokenStorage_ComposesTheHandlerOntoTheApiClient()
    {
        var inner = new CapturingHandler();
        var services = new ServiceCollection();
        services.AddCommonServerTokenStorage();
        services.AddHttpClient("APIClient").ConfigurePrimaryHttpMessageHandler(() => inner);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = VisitorContext(BrowserIp, BrowserUserAgent);

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("APIClient");
        await client.GetAsync(ApiUri, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(BrowserIp);
        inner.LastRequest.Headers.UserAgent.ToString().Should().Be(BrowserUserAgent);
    }

    private static DefaultHttpContext VisitorContext(string remoteIp, string? userAgent = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        return context;
    }

    private static (HttpClient Client, CapturingHandler Inner) CreateClient(HttpContext? httpContext)
    {
        var inner = new CapturingHandler();
        var handler = new BrowserOriginHandler(new HttpContextAccessor { HttpContext = httpContext })
        {
            InnerHandler = inner,
        };

        return (new HttpClient(handler), inner);
    }

    /// <summary>Stub inner handler that records the request the pipeline produced.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
