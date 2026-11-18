using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Grpc.Exceptions;
using MMCA.Common.Grpc.Interceptors;
using MMCA.Common.Shared.Abstractions;
using Moq;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Verifies how <see cref="GrpcResultExceptionInterceptor"/> turns a caught
/// <see cref="ResultFailureException"/> into transport status.
/// <para>
/// The error-carrying case keeps the shared <c>ToRpcException</c> mapping. The empty-errors case
/// (what the message-only constructors produce) used to discard the exception message entirely and
/// answer the placeholder "Unspecified failure", leaving the caller with a failure and no cause.
/// It now keeps <see cref="StatusCode.Internal"/> and carries the real message: synthesizing an
/// <c>Error.Failure</c> instead would have downgraded a server-side fault to
/// <see cref="StatusCode.InvalidArgument"/>, blaming the caller.
/// </para>
/// </summary>
public sealed class GrpcResultExceptionInterceptorTests
{
    private const string Boom = "the handler could not complete";

    private readonly GrpcResultExceptionInterceptor _sut =
        new(NullLogger<GrpcResultExceptionInterceptor>.Instance);

    private readonly FakeServerCallContext _context = new();

    [Theory]
    [InlineData(nameof(GrpcResultExceptionInterceptor.UnaryServerHandler))]
    [InlineData(nameof(GrpcResultExceptionInterceptor.ServerStreamingServerHandler))]
    [InlineData(nameof(GrpcResultExceptionInterceptor.ClientStreamingServerHandler))]
    [InlineData(nameof(GrpcResultExceptionInterceptor.DuplexStreamingServerHandler))]
    public async Task Handler_WhenTheFailureCarriesNoErrors_KeepsInternalAndTheMessage(string handlerKind)
    {
        Func<Task> act = handlerKind switch
        {
            nameof(GrpcResultExceptionInterceptor.UnaryServerHandler) => async () => await _sut.UnaryServerHandler<string, string>(
                "request",
                _context,
                (_, _) => throw new ResultFailureException(Boom)),
            nameof(GrpcResultExceptionInterceptor.ServerStreamingServerHandler) => async () => await _sut.ServerStreamingServerHandler<string, string>(
                "request",
                new Mock<IServerStreamWriter<string>>().Object,
                _context,
                (_, _, _) => throw new ResultFailureException(Boom)),
            nameof(GrpcResultExceptionInterceptor.ClientStreamingServerHandler) => async () => await _sut.ClientStreamingServerHandler<string, string>(
                new Mock<IAsyncStreamReader<string>>().Object,
                _context,
                (_, _) => throw new ResultFailureException(Boom)),
            nameof(GrpcResultExceptionInterceptor.DuplexStreamingServerHandler) => async () => await _sut.DuplexStreamingServerHandler<string, string>(
                new Mock<IAsyncStreamReader<string>>().Object,
                new Mock<IServerStreamWriter<string>>().Object,
                _context,
                (_, _, _) => throw new ResultFailureException(Boom)),
            _ => throw new ArgumentOutOfRangeException(nameof(handlerKind), handlerKind, "Unknown handler kind."),
        };

        RpcException thrown = (await act.Should().ThrowAsync<RpcException>()).Which;
        thrown.StatusCode.Should().Be(StatusCode.Internal, "an empty error list is a server-side fault, not a bad request");
        thrown.Status.Detail.Should().Be(Boom);
    }

    [Fact]
    public async Task UnaryServerHandler_WhenTheFailureHasAnInnerException_AppendsItsMessage()
    {
        Func<Task> act = async () => await _sut.UnaryServerHandler<string, string>(
            "request",
            _context,
            (_, _) => throw new ResultFailureException(Boom, new InvalidOperationException("socket closed")));

        RpcException thrown = (await act.Should().ThrowAsync<RpcException>()).Which;
        thrown.StatusCode.Should().Be(StatusCode.Internal);
        thrown.Status.Detail.Should().Be($"{Boom}: socket closed");
    }

    [Fact]
    public async Task UnaryServerHandler_WhenTheFailureCarriesErrors_UsesTheSharedMappingUnchanged()
    {
        Func<Task> act = async () => await _sut.UnaryServerHandler<string, string>(
            "request",
            _context,
            (_, _) => throw new ResultFailureException([Error.NotFound.WithSource("Handler")]));

        RpcException thrown = (await act.Should().ThrowAsync<RpcException>()).Which;
        thrown.StatusCode.Should().Be(StatusCode.NotFound);
        thrown.Trailers.Should().Contain(entry => entry.Key == "error-0-code");
    }

    [Fact]
    public async Task UnaryServerHandler_WhenTheHandlerSucceeds_PassesTheResponseThrough()
    {
        var response = await _sut.UnaryServerHandler<string, string>(
            "request",
            _context,
            (request, _) => Task.FromResult(request + "-ok"));

        response.Should().Be("request-ok");
    }

    /// <summary>Minimal call context: the interceptor only reads <c>Method</c> for the log line.</summary>
    private sealed class FakeServerCallContext : ServerCallContext
    {
        protected override string MethodCore => "/mmca.Test/Method";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "ipv4:127.0.0.1:5000";

        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);

        protected override Metadata RequestHeadersCore { get; } = [];

        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        protected override Metadata ResponseTrailersCore { get; } = [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
