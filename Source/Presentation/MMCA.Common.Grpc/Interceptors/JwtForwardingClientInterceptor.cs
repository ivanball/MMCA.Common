using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;

namespace MMCA.Common.Grpc.Interceptors;

/// <summary>
/// Client-side gRPC interceptor that copies the inbound <c>Authorization</c> header from the
/// current <see cref="HttpContext"/> onto every outgoing gRPC call's metadata. This forwards
/// the caller's JWT bearer token to downstream services so distributed authorization works
/// across the service mesh without each handler having to thread the token through manually.
/// <para>
/// Registered automatically by <c>AddTypedGrpcClient&lt;TClient&gt;</c> in
/// <see cref="DependencyInjection"/>. The interceptor is a no-op when no
/// <see cref="HttpContext"/> is available (e.g. background processors invoking gRPC clients
/// outside an HTTP request).
/// </para>
/// </summary>
public sealed class JwtForwardingClientInterceptor(IHttpContextAccessor httpContextAccessor) : Interceptor
{
    private const string AuthorizationHeader = "Authorization";

    /// <inheritdoc />
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var newContext = WithForwardedAuthorization(context);
        return continuation(request, newContext);
    }

    /// <inheritdoc />
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var newContext = WithForwardedAuthorization(context);
        return continuation(request, newContext);
    }

    /// <inheritdoc />
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var newContext = WithForwardedAuthorization(context);
        return continuation(request, newContext);
    }

    /// <inheritdoc />
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var newContext = WithForwardedAuthorization(context);
        return continuation(newContext);
    }

    /// <inheritdoc />
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var newContext = WithForwardedAuthorization(context);
        return continuation(newContext);
    }

    private ClientInterceptorContext<TRequest, TResponse> WithForwardedAuthorization<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var authHeader = httpContextAccessor.HttpContext?.Request?.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader))
        {
            return context;
        }

        var headers = context.Options.Headers ?? [];

        // Avoid duplicating the header if a previous interceptor or caller already set it.
        var existing = headers.GetValue(AuthorizationHeader);
        if (existing is not null)
        {
            return context;
        }

        headers.Add(AuthorizationHeader, authHeader);
        var newOptions = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
    }
}
