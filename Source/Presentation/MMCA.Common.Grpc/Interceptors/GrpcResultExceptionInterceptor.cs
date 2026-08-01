using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using MMCA.Common.Grpc.Exceptions;

namespace MMCA.Common.Grpc.Interceptors;

/// <summary>
/// Server-side gRPC interceptor that catches <see cref="ResultFailureException"/> thrown by
/// service implementations and rethrows them as <see cref="RpcException"/> with the appropriate
/// <see cref="StatusCode"/> and structured error trailers. This mirrors the behavior of
/// <c>ApiControllerBase.HandleFailure</c> for HTTP responses, keeping cross-service error
/// surfacing consistent across both transports.
/// <para>
/// Register via <c>services.AddGrpc(o =&gt; o.Interceptors.Add&lt;GrpcResultExceptionInterceptor&gt;())</c>
/// or use the <c>AddGrpcServiceDefaults</c> extension which wires it automatically.
/// </para>
/// </summary>
public sealed partial class GrpcResultExceptionInterceptor(ILogger<GrpcResultExceptionInterceptor> logger) : Interceptor
{
    /// <inheritdoc />
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        catch (ResultFailureException ex)
        {
            LogResultFailure(logger, context.Method, ex);
            throw ToTransportException(ex);
        }
    }

    /// <inheritdoc />
    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        catch (ResultFailureException ex)
        {
            LogResultFailure(logger, context.Method, ex);
            throw ToTransportException(ex);
        }
    }

    /// <inheritdoc />
    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await continuation(requestStream, context).ConfigureAwait(false);
        }
        catch (ResultFailureException ex)
        {
            LogResultFailure(logger, context.Method, ex);
            throw ToTransportException(ex);
        }
    }

    /// <inheritdoc />
    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await continuation(requestStream, responseStream, context).ConfigureAwait(false);
        }
        catch (ResultFailureException ex)
        {
            LogResultFailure(logger, context.Method, ex);
            throw ToTransportException(ex);
        }
    }

    /// <summary>
    /// Builds the transport exception for a caught <see cref="ResultFailureException"/>, shared by
    /// all four handler shapes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the exception carries errors, the standard <c>ToRpcException</c> mapping applies and the
    /// structured trailers come with it.
    /// </para>
    /// <para>
    /// When it carries none, which is what the message-only constructors produce, that mapping
    /// answers the placeholder detail "Unspecified failure" and the real
    /// <see cref="Exception.Message"/> is lost: the caller sees a failure with no cause. Synthesizing
    /// an <c>Error.Failure</c> to carry the message is not the fix, because <c>ErrorType.Failure</c>
    /// maps to <see cref="StatusCode.InvalidArgument"/>
    /// (<c>ResultGrpcExtensions</c>), which would blame the caller for a server-side fault. So the
    /// empty case keeps <see cref="StatusCode.Internal"/>, the status the shared mapping already
    /// chooses for an empty list, and replaces only the detail. An inner exception's message is
    /// appended, since it is usually the one that names the actual cause.
    /// </para>
    /// </remarks>
    /// <param name="exception">The caught result-failure exception.</param>
    /// <returns>The <see cref="RpcException"/> to surface to the caller.</returns>
    private static RpcException ToTransportException(ResultFailureException exception)
    {
        if (exception.Errors.Count > 0)
        {
            return exception.Errors.ToRpcException();
        }

        var detail = exception.InnerException is null
            ? exception.Message
            : string.Concat(exception.Message, ": ", exception.InnerException.Message);

        return new RpcException(new Status(StatusCode.Internal, detail));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC method {Method} returned a result failure")]
    private static partial void LogResultFailure(ILogger logger, string method, ResultFailureException exception);
}
