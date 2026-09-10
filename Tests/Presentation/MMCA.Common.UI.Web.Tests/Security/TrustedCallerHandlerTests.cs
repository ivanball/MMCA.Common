using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Web.Security;

namespace MMCA.Common.UI.Web.Tests.Security;

/// <summary>
/// Pins <see cref="TrustedCallerHandler"/>: the secret rides a request whose scheme, host and port
/// match the gateway origin, as exactly one header value, and rides nothing else. A client later
/// pointed at a third party (a different host, or the same host on a different port) must never be
/// handed the bypass.
/// </summary>
public sealed class TrustedCallerHandlerTests
{
    private const string HeaderName = "X-Internal-Caller-Key";
    private const string Secret = "0123456789abcdef0123456789abcdef";
    private const string GatewayOrigin = "https://gateway.example.com:6001";

    [Fact]
    public async Task SendAsync_ToTheGatewayOrigin_AddsExactlyOneHeaderValue()
    {
        var (client, inner) = CreateClient();

        await client.GetAsync(new Uri(GatewayOrigin + "/Auth/refresh"), TestContext.Current.CancellationToken);

        inner.LastRequest.Should().NotBeNull();
        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(Secret);
    }

    [Fact]
    public async Task SendAsync_ToAnotherPathOnTheGatewayOrigin_StillAddsTheHeader()
    {
        var (client, inner) = CreateClient();

        await client.GetAsync(new Uri(GatewayOrigin + "/Events?page=2"), TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.Contains(HeaderName).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://telemetry.example.com/ingest")] // different host
    [InlineData("https://gateway.example.com/Auth/refresh")] // same host, default port
    [InlineData("https://gateway.example.com:6002/Auth")] // same host, different port
    [InlineData("http://gateway.example.com:6001/Auth")] // same host and port, different scheme
    public async Task SendAsync_ToAnyOtherOrigin_AddsNoHeader(string requestUri)
    {
        var (client, inner) = CreateClient();

        await client.GetAsync(new Uri(requestUri), TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.Contains(HeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenTheRequestAlreadyCarriesTheHeader_ReplacesItRatherThanAppending()
    {
        var (client, inner) = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GatewayOrigin + "/Auth/refresh"));
        request.Headers.TryAddWithoutValidation(HeaderName, "a-forged-value");

        await client.SendAsync(request, TestContext.Current.CancellationToken);

        inner.LastRequest!.Headers.GetValues(HeaderName).Should().ContainSingle().Which.Should().Be(Secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithABlankHeaderNameOrSecret_Throws(string? blank)
    {
        var origin = new Uri(GatewayOrigin);

        FluentActions.Invoking(() => new TrustedCallerHandler(blank!, Secret, origin))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new TrustedCallerHandler(HeaderName, blank!, origin))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithNoGatewayOrigin_Throws() =>
        FluentActions.Invoking(() => new TrustedCallerHandler(HeaderName, Secret, null!))
            .Should().Throw<ArgumentNullException>();

    private static (HttpClient Client, CapturingHandler Inner) CreateClient()
    {
        var inner = new CapturingHandler();
        var handler = new TrustedCallerHandler(HeaderName, Secret, new Uri(GatewayOrigin))
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
