using System.Security.Claims;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Grpc.Authentication;
using MMCA.Common.Grpc.Interceptors;
using Moq;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Pins the service-token fallback of <see cref="JwtForwardingClientInterceptor"/>. The user's token
/// always wins (the inbound header, or the access token saved on the authentication ticket); only a
/// call that has no user token gets <c>Bearer {serviceToken}</c> from the registered
/// <see cref="IServiceTokenProvider"/>; with no provider the interceptor behaves exactly as before;
/// and an <c>Authorization</c> entry the caller set itself is never overwritten.
/// </summary>
public sealed class JwtForwardingClientInterceptorServiceTokenTests
{
    private const string AuthorizationHeader = "Authorization";
    private const string UserBearer = "Bearer user-token-123";
    private const string ServiceToken = "service-token-456";
    private const string ServiceBearer = "Bearer " + ServiceToken;

    private static readonly Method<FakeRequest, FakeResponse> TestMethod = new(
        MethodType.Unary,
        "TestService",
        "TestMethod",
        new Marshaller<FakeRequest>(_ => [], _ => new FakeRequest()),
        new Marshaller<FakeResponse>(_ => [], _ => new FakeResponse()));

    // -- User token present: forwarded, the provider is never consulted --
    [Fact]
    public void AsyncUnaryCall_WithInboundUserHeaderAndAProvider_ForwardsTheUserTokenAndNeverMintsAServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(HttpContextWithHeader(UserBearer), provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(UserBearer,
            "the user's identity must reach the downstream service whenever the request carries one");
        CountAuthorizationEntries(headers).Should().Be(1);
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Fact]
    public void AsyncUnaryCall_WithASavedUserAccessTokenAndAProvider_ForwardsTheSavedTokenAndNeverMintsAServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(HttpContextWithSavedToken("user-token-123"), provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(UserBearer,
            "a token that arrived by query string (SignalR) is still the user's token");
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    // -- No user token: the service token is attached --
    [Fact]
    public void AsyncUnaryCall_WithoutHttpContextAndWithAProvider_AttachesTheServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(httpContext: null, provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().NotBeNull("a background processor calling another service must still authenticate");
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
        CountAuthorizationEntries(headers).Should().Be(1);
    }

    [Fact]
    public void AsyncUnaryCall_WithAnHttpContextCarryingNoUserTokenAndWithAProvider_AttachesTheServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(new DefaultHttpContext(), provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer,
            "an anonymous inbound request that fans out internally calls as the service, not as nobody");
    }

    [Fact]
    public void AsyncUnaryCall_WithoutHttpContextAndWithAProvider_KeepsUnrelatedHeaders()
    {
        var provider = CreateProvider();
        var sut = CreateSut(httpContext: null, provider.Object);
        var existingHeaders = new Metadata { { "x-custom", "custom-value" } };

        var headers = CaptureUnaryHeaders(sut, CreateContext(existingHeaders));

        headers.Should().NotBeNull();
        headers.GetValue("x-custom").Should().Be("custom-value");
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
    }

    // -- No provider and no user token: unchanged behavior --
    [Fact]
    public void AsyncUnaryCall_WithoutAProviderAndWithoutAUserToken_AttachesNothing()
    {
        var sut = CreateSut(new DefaultHttpContext(), serviceTokenProvider: null);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().BeNull("a host that never registered a provider keeps the pre-service-token behavior");
    }

    [Fact]
    public void AsyncUnaryCall_WithoutAProviderAndWithoutHttpContext_AttachesNothing()
    {
        var sut = CreateSut(httpContext: null, serviceTokenProvider: null);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().BeNull();
    }

    // -- Caller-set Authorization is never overwritten --
    [Fact]
    public void AsyncUnaryCall_WhenTheCallerSetAuthorization_KeepsItEvenWithAProvider()
    {
        var provider = CreateProvider();
        var sut = CreateSut(httpContext: null, provider.Object);
        var existingHeaders = new Metadata { { AuthorizationHeader, "Bearer caller-value" } };

        var headers = CaptureUnaryHeaders(sut, CreateContext(existingHeaders));

        headers.Should().NotBeNull();
        CountAuthorizationEntries(headers).Should().Be(1);
        headers.GetValue(AuthorizationHeader).Should().Be("Bearer caller-value",
            "a caller that chose its own credential must not have it replaced by the service token");
    }

    [Fact]
    public void AsyncUnaryCall_WhenTheCallerSetAuthorizationOnAnAnonymousRequest_KeepsItEvenWithAProvider()
    {
        var provider = CreateProvider();
        var sut = CreateSut(new DefaultHttpContext(), provider.Object);
        var existingHeaders = new Metadata { { AuthorizationHeader, "Bearer caller-value" } };

        var headers = CaptureUnaryHeaders(sut, CreateContext(existingHeaders));

        headers.Should().NotBeNull();
        CountAuthorizationEntries(headers).Should().Be(1);
        headers.GetValue(AuthorizationHeader).Should().Be("Bearer caller-value");
    }

    // -- Other call shapes --
    [Fact]
    public void BlockingUnaryCall_WithoutHttpContextAndWithAProvider_AttachesTheServiceToken()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        sut.BlockingUnaryCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return new FakeResponse();
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
    }

    [Fact]
    public void AsyncServerStreamingCall_WithoutHttpContextAndWithAProvider_AttachesTheServiceToken()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncServerStreamingCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return new AsyncServerStreamingCall<FakeResponse>(
                    new FakeStreamReader(),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
    }

    [Fact]
    public void AsyncServerStreamingCall_WithInboundUserHeaderAndAProvider_ForwardsTheUserToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(HttpContextWithHeader(UserBearer), provider.Object);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncServerStreamingCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return new AsyncServerStreamingCall<FakeResponse>(
                    new FakeStreamReader(),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(UserBearer);
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Fact]
    public void AsyncClientStreamingCall_WithoutHttpContextAndWithAProvider_AttachesTheServiceToken()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncClientStreamingCall(
            CreateContext(),
            ctx =>
            {
                captured = ctx;
                return new AsyncClientStreamingCall<FakeRequest, FakeResponse>(
                    new FakeStreamWriter(),
                    Task.FromResult(new FakeResponse()),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
    }

    [Fact]
    public void AsyncDuplexStreamingCall_WithoutHttpContextAndWithAProvider_AttachesTheServiceToken()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncDuplexStreamingCall(
            CreateContext(),
            ctx =>
            {
                captured = ctx;
                return new AsyncDuplexStreamingCall<FakeRequest, FakeResponse>(
                    new FakeStreamWriter(),
                    new FakeStreamReader(),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
    }

    [Fact]
    public void AsyncDuplexStreamingCall_WithoutAProviderAndWithoutAUserToken_AttachesNothing()
    {
        var sut = CreateSut(httpContext: null, serviceTokenProvider: null);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncDuplexStreamingCall(
            CreateContext(),
            ctx =>
            {
                captured = ctx;
                return new AsyncDuplexStreamingCall<FakeRequest, FakeResponse>(
                    new FakeStreamWriter(),
                    new FakeStreamReader(),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers.Should().BeNull();
    }

    // -- Scaffolding --
    private static Mock<IServiceTokenProvider> CreateProvider()
    {
        var provider = new Mock<IServiceTokenProvider>();
        provider.Setup(p => p.GetToken()).Returns(ServiceToken);
        return provider;
    }

    private static JwtForwardingClientInterceptor CreateSut(HttpContext? httpContext, IServiceTokenProvider? serviceTokenProvider)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);
        return new JwtForwardingClientInterceptor(accessor.Object, serviceTokenProvider);
    }

    private static DefaultHttpContext HttpContextWithHeader(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = value;
        return context;
    }

    private static DefaultHttpContext HttpContextWithSavedToken(string accessToken)
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = accessToken }]);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "42")], "Bearer")),
            properties,
            "Bearer");
        var context = new DefaultHttpContext();
        context.Features.Set<IAuthenticateResultFeature>(new AuthenticateResultFeatureStub(AuthenticateResult.Success(ticket)));
        return context;
    }

    private static ClientInterceptorContext<FakeRequest, FakeResponse> CreateContext(Metadata? headers = null) =>
        new(TestMethod, host: null, new CallOptions(headers));

    private static Metadata? CaptureUnaryHeaders(
        JwtForwardingClientInterceptor sut,
        ClientInterceptorContext<FakeRequest, FakeResponse> context)
    {
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            context,
            (_, ctx) =>
            {
                captured = ctx;
                return new AsyncUnaryCall<FakeResponse>(
                    Task.FromResult(new FakeResponse()),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => [],
                    () => { });
            });

        captured.Should().NotBeNull();
        return captured.Value.Options.Headers;
    }

    private static int CountAuthorizationEntries(Metadata headers) =>
        headers.Count(e => string.Equals(e.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase));
}
