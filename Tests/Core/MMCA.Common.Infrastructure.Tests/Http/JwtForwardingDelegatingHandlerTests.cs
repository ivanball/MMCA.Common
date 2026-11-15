using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Infrastructure.Http;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Http;

/// <summary>
/// The handler forwards the inbound request's <c>Authorization</c> header to the outgoing request as a
/// Bearer credential, leaves an explicitly set header alone, and is a no-op outside an HTTP request.
/// </summary>
public sealed class JwtForwardingDelegatingHandlerTests
{
    [Fact]
    public async Task InboundBearerHeader_IsForwardedWithTheSameToken()
    {
        var outgoing = await ForwardAsync(InboundAuthorization("Bearer abc.def.ghi"));

        outgoing.Headers.Authorization.Should().NotBeNull();
        outgoing.Headers.Authorization!.Scheme.Should().Be("Bearer");
        outgoing.Headers.Authorization.Parameter.Should().Be("abc.def.ghi");
    }

    [Fact]
    public async Task InboundBearerPrefix_IsMatchedCaseInsensitively()
    {
        var outgoing = await ForwardAsync(InboundAuthorization("bearer token-1"));

        outgoing.Headers.Authorization!.Scheme.Should().Be("Bearer");
        outgoing.Headers.Authorization.Parameter.Should().Be("token-1", "the lower-case prefix is stripped, not doubled");
    }

    [Fact]
    public async Task InboundHeaderWithoutBearerPrefix_IsSentWhole_UnderTheBearerScheme()
    {
        var outgoing = await ForwardAsync(InboundAuthorization("raw-token-value"));

        outgoing.Headers.Authorization!.Scheme.Should().Be("Bearer");
        outgoing.Headers.Authorization.Parameter.Should().Be("raw-token-value");
    }

    [Fact]
    public async Task BearerWordWithoutTheSeparatingSpace_IsNotTreatedAsThePrefix()
    {
        var outgoing = await ForwardAsync(InboundAuthorization("Bearerxyz"));

        outgoing.Headers.Authorization!.Parameter.Should().Be("Bearerxyz", "only \"Bearer \" with its space is the scheme prefix");
    }

    [Fact]
    public async Task ExplicitAuthorizationOnTheOutgoingRequest_IsLeftUntouched()
    {
        var outgoing = await ForwardAsync(
            InboundAuthorization("Bearer inbound-token"),
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "explicit"));

        outgoing.Headers.Authorization!.Scheme.Should().Be("Basic");
        outgoing.Headers.Authorization.Parameter.Should().Be("explicit");
    }

    [Fact]
    public async Task NoHttpContext_SendsWithoutAuthorization()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns((HttpContext?)null);

        var outgoing = await ForwardAsync(accessor.Object);

        outgoing.Headers.Authorization.Should().BeNull("a background caller has no inbound credential to forward");
    }

    [Fact]
    public async Task InboundRequestWithoutAuthorization_SendsWithoutAuthorization()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(new DefaultHttpContext());

        var outgoing = await ForwardAsync(accessor.Object);

        outgoing.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task NullRequest_Throws()
    {
        using var handler = new JwtForwardingDelegatingHandler(Mock.Of<IHttpContextAccessor>())
        {
            InnerHandler = new CapturingHandler(),
        };
        using var invoker = new HttpMessageInvoker(handler);

        var act = () => invoker.SendAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static IHttpContextAccessor InboundAuthorization(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = value;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(a => a.HttpContext).Returns(context);
        return accessor.Object;
    }

    private static async Task<HttpRequestMessage> ForwardAsync(
        IHttpContextAccessor accessor,
        Action<HttpRequestMessage>? prepare = null)
    {
        var inner = new CapturingHandler();
        using var handler = new JwtForwardingDelegatingHandler(accessor) { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://downstream.test/api/items");
        prepare?.Invoke(request);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the request must reach the inner handler on every path");
        return inner.Received!;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Received { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
