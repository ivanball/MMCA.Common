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
            throw ex.Errors.ToRpcException();
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
            throw ex.Errors.ToRpcException();
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
            throw ex.Errors.ToRpcException();
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
            throw ex.Errors.ToRpcException();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC method {Method} returned a result failure")]
    private static partial void LogResultFailure(ILogger logger, string method, ResultFailureException exception);
}
