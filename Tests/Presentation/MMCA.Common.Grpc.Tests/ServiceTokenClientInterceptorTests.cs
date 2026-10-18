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
/// Pins <see cref="ServiceTokenClientInterceptor"/>, the per-client service-token opt-in. A call with no
/// user token gets exactly one <c>Bearer {serviceToken}</c> from the registered
/// <see cref="IServiceTokenProvider"/>; a call whose ambient request carries a user token (the inbound
/// header, or the access token saved on the authentication ticket) gets nothing from this interceptor
/// and never costs a token mint; an <c>Authorization</c> entry the caller set itself is never
/// overwritten. Chained with <see cref="JwtForwardingClientInterceptor"/> in either order, the user's
/// token wins when there is one, the service token is sent when there is none, and the call always
/// carries exactly one <c>Authorization</c> entry.
/// </summary>
public sealed class ServiceTokenClientInterceptorTests
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

    // -- No user token: the service token is attached --
    [Fact]
    public void AsyncUnaryCall_WithoutHttpContext_AttachesExactlyOneServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(httpContext: null, provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().NotBeNull("a background processor calling an opted-in service must still authenticate");
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
        CountAuthorizationEntries(headers).Should().Be(1);
        provider.Verify(p => p.GetToken(), Times.Once);
    }

    [Fact]
    public void AsyncUnaryCall_WithAnHttpContextCarryingNoUserToken_AttachesTheServiceToken()
    {
        var sut = CreateSut(new DefaultHttpContext(), CreateProvider().Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer,
            "an anonymous inbound request that fans out internally calls as the service, not as nobody");
        CountAuthorizationEntries(headers).Should().Be(1);
    }

    [Fact]
    public void AsyncUnaryCall_WithoutHttpContext_KeepsUnrelatedHeaders()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);
        var existingHeaders = new Metadata { { "x-custom", "custom-value" } };

        var headers = CaptureUnaryHeaders(sut, CreateContext(existingHeaders));

        headers.Should().NotBeNull();
        headers.GetValue("x-custom").Should().Be("custom-value", "attaching the service token must not drop the caller's metadata");
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
        CountAuthorizationEntries(headers).Should().Be(1);
    }

    [Fact]
    public void AsyncUnaryCall_WhenTheProviderReturnsAnEmptyToken_AttachesNothing()
    {
        var provider = new Mock<IServiceTokenProvider>();
        provider.Setup(p => p.GetToken()).Returns(string.Empty);
        var sut = CreateSut(httpContext: null, provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().BeNull("an empty token must never go out as a bare \"Bearer \" header");
    }

    // -- User token present: nothing attached, the provider is never consulted --
    [Fact]
    public void AsyncUnaryCall_WithInboundUserHeader_AttachesNothingAndNeverMintsAServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(HttpContextWithHeader(UserBearer), provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().BeNull("forwarding the user's token is the JWT-forwarding interceptor's job, not this one's");
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Fact]
    public void AsyncUnaryCall_WithASavedUserAccessToken_AttachesNothingAndNeverMintsAServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(HttpContextWithSavedToken("user-token-123"), provider.Object);

        var headers = CaptureUnaryHeaders(sut, CreateContext());

        headers.Should().BeNull("a token that arrived by query string (SignalR) is still the user's token");
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    // -- Caller-set Authorization is never overwritten --
    [Fact]
    public void AsyncUnaryCall_WhenTheCallerSetAuthorization_KeepsItAndNeverMintsAServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(httpContext: null, provider.Object);
        var existingHeaders = new Metadata { { AuthorizationHeader, "Bearer caller-value" } };

        var headers = CaptureUnaryHeaders(sut, CreateContext(existingHeaders));

        headers.Should().NotBeNull();
        CountAuthorizationEntries(headers).Should().Be(1);
        headers.GetValue(AuthorizationHeader).Should().Be("Bearer caller-value",
            "a caller that chose its own credential must not have it replaced by the service token");
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Fact]
    public void AsyncUnaryCall_WhenTheCallerSetAuthorizationOnAnAnonymousRequest_KeepsItAndNeverMintsAServiceToken()
    {
        var provider = CreateProvider();
        var sut = CreateSut(new DefaultHttpContext(), provider.Object);
        var existingHeaders = new Metadata { { AuthorizationHeader, "Bearer caller-value" } };

        var headers = CaptureUnaryHeaders(sut, CreateContext(existingHeaders));

        headers.Should().NotBeNull();
        CountAuthorizationEntries(headers).Should().Be(1);
        headers.GetValue(AuthorizationHeader).Should().Be("Bearer caller-value");
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    // -- Null continuation guard --
    [Fact]
    public void AsyncUnaryCall_WithNullContinuation_ThrowsArgumentNullException()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);

        var act = () => sut.AsyncUnaryCall(new FakeRequest(), CreateContext(), continuation: null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("continuation");
    }

    // -- Other call shapes --
    [Fact]
    public void BlockingUnaryCall_WithoutHttpContext_AttachesTheServiceToken()
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
        CountAuthorizationEntries(captured.Value.Options.Headers!).Should().Be(1);
    }

    [Fact]
    public void AsyncServerStreamingCall_WithoutHttpContext_AttachesTheServiceToken()
    {
        var sut = CreateSut(httpContext: null, CreateProvider().Object);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncServerStreamingCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return CreateServerStreamingCall();
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
        CountAuthorizationEntries(captured.Value.Options.Headers!).Should().Be(1);
    }

    [Fact]
    public void AsyncServerStreamingCall_WithInboundUserHeader_AttachesNothingAndNeverMintsAServiceToken()
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
                return CreateServerStreamingCall();
            });

        captured.Should().NotBeNull();
        captured.Value.Options.Headers.Should().BeNull();
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Fact]
    public void AsyncClientStreamingCall_WithoutHttpContext_AttachesTheServiceToken()
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
        CountAuthorizationEntries(captured.Value.Options.Headers!).Should().Be(1);
    }

    [Fact]
    public void AsyncDuplexStreamingCall_WithoutHttpContext_AttachesTheServiceToken()
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
        CountAuthorizationEntries(captured.Value.Options.Headers!).Should().Be(1);
    }

    // -- Chained with the JWT-forwarding interceptor: the outcome does not depend on order --
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chained_WithInboundUserHeader_SendsOnlyTheUserTokenInEitherOrder(bool serviceTokenRunsFirst)
    {
        var provider = CreateProvider();

        var headers = CaptureChainedUnaryHeaders(HttpContextWithHeader(UserBearer), provider.Object, serviceTokenRunsFirst);

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(UserBearer,
            "the user's token wins over the service token whichever interceptor runs first");
        CountAuthorizationEntries(headers).Should().Be(1);
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chained_WithASavedUserAccessToken_SendsOnlyTheUserTokenInEitherOrder(bool serviceTokenRunsFirst)
    {
        var provider = CreateProvider();

        var headers = CaptureChainedUnaryHeaders(HttpContextWithSavedToken("user-token-123"), provider.Object, serviceTokenRunsFirst);

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(UserBearer);
        CountAuthorizationEntries(headers).Should().Be(1);
        provider.Verify(p => p.GetToken(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chained_WithoutHttpContext_SendsOnlyTheServiceTokenInEitherOrder(bool serviceTokenRunsFirst)
    {
        var headers = CaptureChainedUnaryHeaders(httpContext: null, CreateProvider().Object, serviceTokenRunsFirst);

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer,
            "with no user token the opted-in client calls as the service whichever interceptor runs first");
        CountAuthorizationEntries(headers).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chained_WithAnHttpContextCarryingNoUserToken_SendsOnlyTheServiceTokenInEitherOrder(bool serviceTokenRunsFirst)
    {
        var headers = CaptureChainedUnaryHeaders(new DefaultHttpContext(), CreateProvider().Object, serviceTokenRunsFirst);

        headers.Should().NotBeNull();
        headers.GetValue(AuthorizationHeader).Should().Be(ServiceBearer);
        CountAuthorizationEntries(headers).Should().Be(1);
    }

    // -- Scaffolding --
    private static Mock<IServiceTokenProvider> CreateProvider()
    {
        var provider = new Mock<IServiceTokenProvider>();
        provider.Setup(p => p.GetToken()).Returns(ServiceToken);
        return provider;
    }

    private static IHttpContextAccessor CreateAccessor(HttpContext? httpContext)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);
        return accessor.Object;
    }

    private static ServiceTokenClientInterceptor CreateSut(HttpContext? httpContext, IServiceTokenProvider serviceTokenProvider) =>
        new(CreateAccessor(httpContext), serviceTokenProvider);

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

    private static AsyncUnaryCall<FakeResponse> CreateAsyncUnaryCall() =>
        new(
            Task.FromResult(new FakeResponse()),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

    private static AsyncServerStreamingCall<FakeResponse> CreateServerStreamingCall() =>
        new(
            new FakeStreamReader(),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

    private static Metadata? CaptureUnaryHeaders(
        Interceptor sut,
        ClientInterceptorContext<FakeRequest, FakeResponse> context)
    {
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            context,
            (_, ctx) =>
            {
                captured = ctx;
                return CreateAsyncUnaryCall();
            });

        captured.Should().NotBeNull();
        return captured.Value.Options.Headers;
    }

    /// <summary>
    /// Runs a unary call through both interceptors the way a client's interceptor chain does: the outer
    /// interceptor's continuation invokes the inner one, whose continuation is the transport.
    /// </summary>
    private static Metadata? CaptureChainedUnaryHeaders(
        HttpContext? httpContext,
        IServiceTokenProvider serviceTokenProvider,
        bool serviceTokenRunsFirst)
    {
        var accessor = CreateAccessor(httpContext);
        Interceptor serviceToken = new ServiceTokenClientInterceptor(accessor, serviceTokenProvider);
        Interceptor jwtForwarding = new JwtForwardingClientInterceptor(accessor);
        var (outer, inner) = serviceTokenRunsFirst ? (serviceToken, jwtForwarding) : (jwtForwarding, serviceToken);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = outer.AsyncUnaryCall(
            new FakeRequest(),
            CreateContext(),
            (request, outerContext) => inner.AsyncUnaryCall(
                request,
                outerContext,
                (_, innerContext) =>
                {
                    captured = innerContext;
                    return CreateAsyncUnaryCall();
                }));

        captured.Should().NotBeNull();
        return captured.Value.Options.Headers;
    }

    private static int CountAuthorizationEntries(Metadata headers) =>
        headers.Count(e => string.Equals(e.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase));
}
