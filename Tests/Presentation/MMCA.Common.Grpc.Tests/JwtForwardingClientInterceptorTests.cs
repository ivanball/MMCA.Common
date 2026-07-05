using AwesomeAssertions;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Grpc.Interceptors;
using Moq;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Exercises <see cref="JwtForwardingClientInterceptor"/> directly with hand-built
/// <see cref="ClientInterceptorContext{TRequest, TResponse}"/> instances and capturing
/// continuation delegates (no gRPC channel involved). The inbound Authorization header must be
/// copied onto the outgoing call metadata for every call shape, calls without an ambient
/// HttpContext or bearer token must pass through untouched, and an Authorization entry already
/// present on the call must never be overwritten or duplicated.
/// </summary>
public sealed class JwtForwardingClientInterceptorTests
{
    private const string BearerToken = "Bearer test-token-123";
    private const string AuthorizationHeader = "Authorization";

    private static readonly Method<FakeRequest, FakeResponse> TestMethod = new(
        MethodType.Unary,
        "TestService",
        "TestMethod",
        new Marshaller<FakeRequest>(_ => [], _ => new FakeRequest()),
        new Marshaller<FakeResponse>(_ => [], _ => new FakeResponse()));

    // ── Factory ──
    private static JwtForwardingClientInterceptor CreateSut(HttpContext? httpContext)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);
        return new JwtForwardingClientInterceptor(accessor.Object);
    }

    private static DefaultHttpContext CreateHttpContextWithAuthorization(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = value;
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

    private static int CountAuthorizationEntries(Metadata headers) =>
        headers.Count(e => string.Equals(e.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase));

    // ── AsyncUnaryCall: bearer token present ──
    [Fact]
    public void AsyncUnaryCall_WithInboundBearerToken_ForwardsAuthorizationHeader()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return CreateAsyncUnaryCall();
            });

        captured.Should().NotBeNull();
        var headers = captured!.Value.Options.Headers;
        headers.Should().NotBeNull("the interceptor must attach metadata carrying the forwarded token");
        headers!.GetValue(AuthorizationHeader).Should().Be(BearerToken);
    }

    // ── AsyncUnaryCall: no ambient HttpContext ──
    [Fact]
    public void AsyncUnaryCall_WithoutHttpContext_PassesCallOptionsThroughUnchanged()
    {
        var sut = CreateSut(httpContext: null);
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return CreateAsyncUnaryCall();
            });

        captured.Should().NotBeNull();
        captured!.Value.Options.Headers.Should().BeNull(
            "background processors without an HTTP request must not gain an Authorization header");
    }

    // ── AsyncUnaryCall: HttpContext without an Authorization header ──
    [Fact]
    public void AsyncUnaryCall_WithoutInboundAuthorizationHeader_PassesCallOptionsThroughUnchanged()
    {
        var sut = CreateSut(new DefaultHttpContext());
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            CreateContext(),
            (_, ctx) =>
            {
                captured = ctx;
                return CreateAsyncUnaryCall();
            });

        captured.Should().NotBeNull();
        captured!.Value.Options.Headers.Should().BeNull();
    }

    // ── AsyncUnaryCall: caller already set an Authorization header ──
    [Fact]
    public void AsyncUnaryCall_WhenCallAlreadyCarriesAuthorization_KeepsExistingValueWithoutDuplicating()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
        var existingHeaders = new Metadata { { AuthorizationHeader, "Bearer existing-value" } };
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            CreateContext(existingHeaders),
            (_, ctx) =>
            {
                captured = ctx;
                return CreateAsyncUnaryCall();
            });

        captured.Should().NotBeNull();
        var headers = captured!.Value.Options.Headers;
        headers.Should().BeSameAs(existingHeaders, "an already-authorized call passes through untouched");
        CountAuthorizationEntries(headers!).Should().Be(1);
        headers!.GetValue(AuthorizationHeader).Should().Be("Bearer existing-value");
    }

    // ── AsyncUnaryCall: unrelated headers survive the forwarding ──
    [Fact]
    public void AsyncUnaryCall_WithUnrelatedExistingHeaders_AppendsAuthorizationAndKeepsThem()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
        var existingHeaders = new Metadata { { "x-custom", "custom-value" } };
        ClientInterceptorContext<FakeRequest, FakeResponse>? captured = null;

        using var call = sut.AsyncUnaryCall(
            new FakeRequest(),
            CreateContext(existingHeaders),
            (_, ctx) =>
            {
                captured = ctx;
                return CreateAsyncUnaryCall();
            });

        captured.Should().NotBeNull();
        var headers = captured!.Value.Options.Headers;
        headers.Should().NotBeNull();
        headers!.GetValue("x-custom").Should().Be("custom-value");
        headers.GetValue(AuthorizationHeader).Should().Be(BearerToken);
        CountAuthorizationEntries(headers).Should().Be(1);
    }

    // ── AsyncUnaryCall: null continuation guard ──
    [Fact]
    public void AsyncUnaryCall_WithNullContinuation_ThrowsArgumentNullException()
    {
        var sut = CreateSut(httpContext: null);

        var act = () => sut.AsyncUnaryCall(new FakeRequest(), CreateContext(), continuation: null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("continuation");
    }

    // ── BlockingUnaryCall ──
    [Fact]
    public void BlockingUnaryCall_WithInboundBearerToken_ForwardsAuthorizationHeader()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
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
        captured!.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(BearerToken);
    }

    // ── AsyncServerStreamingCall ──
    [Fact]
    public void AsyncServerStreamingCall_WithInboundBearerToken_ForwardsAuthorizationHeader()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
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
        captured!.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(BearerToken);
    }

    // ── AsyncClientStreamingCall ──
    [Fact]
    public void AsyncClientStreamingCall_WithInboundBearerToken_ForwardsAuthorizationHeader()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
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
        captured!.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(BearerToken);
    }

    // ── AsyncDuplexStreamingCall ──
    [Fact]
    public void AsyncDuplexStreamingCall_WithInboundBearerToken_ForwardsAuthorizationHeader()
    {
        var sut = CreateSut(CreateHttpContextWithAuthorization(BearerToken));
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
        captured!.Value.Options.Headers!.GetValue(AuthorizationHeader).Should().Be(BearerToken);
    }
}

// ── Test message/stream stand-ins (marshaller and stream behavior is never exercised) ──
internal sealed class FakeRequest;

internal sealed class FakeResponse;

internal sealed class FakeStreamReader : IAsyncStreamReader<FakeResponse>
{
    public FakeResponse Current => new();

    public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class FakeStreamWriter : IClientStreamWriter<FakeRequest>
{
    public WriteOptions? WriteOptions { get; set; }

    public Task CompleteAsync() => Task.CompletedTask;

    public Task WriteAsync(FakeRequest message) => Task.CompletedTask;
}
