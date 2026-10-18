using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Grpc.Authentication;

namespace MMCA.Common.Grpc.Interceptors;

/// <summary>
/// Client-side gRPC interceptor that sends a call with no user token as <c>Bearer {serviceToken}</c>,
/// so a background processor or an anonymous request that fans out internally authenticates to the
/// downstream service as this service.
/// <para>
/// Added per client by <see cref="ServiceTokenHttpClientBuilderExtensions"/>' <c>WithServiceToken()</c>,
/// never globally: a service token goes only to a service that validates it
/// (<c>AddServiceTokenAuthentication</c>). A service that does not would have to reject it, and a
/// default JwtBearer scheme first fetches its OIDC metadata to do so, which can stall a call made
/// while that metadata endpoint is still starting.
/// </para>
/// <para>
/// Precedence is the user's token, then the service token. When the ambient request carries a user
/// token this interceptor attaches nothing and leaves it to <see cref="JwtForwardingClientInterceptor"/>,
/// so the outcome does not depend on the order the two run in. An <c>Authorization</c> entry already on
/// the call is never overwritten or duplicated, and is checked first so it never costs a token mint.
/// </para>
/// </summary>
/// <param name="httpContextAccessor">Accessor for the ambient request, read only to detect a user token.</param>
/// <param name="serviceTokenProvider">The service-token source registered by <c>AddServiceTokenClient</c>.</param>
public sealed class ServiceTokenClientInterceptor(
    IHttpContextAccessor httpContextAccessor,
    IServiceTokenProvider serviceTokenProvider) : Interceptor
{
    /// <inheritdoc />
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(request, WithServiceAuthorization(context));
    }

    /// <inheritdoc />
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(request, WithServiceAuthorization(context));
    }

    /// <inheritdoc />
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(request, WithServiceAuthorization(context));
    }

    /// <inheritdoc />
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(WithServiceAuthorization(context));
    }

    /// <inheritdoc />
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return continuation(WithServiceAuthorization(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> WithServiceAuthorization<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        if (JwtForwardingClientInterceptor.HasAuthorization(context)
            || JwtForwardingClientInterceptor.ResolveUserAuthorization(httpContextAccessor.HttpContext) is not null)
        {
            return context;
        }

        var token = serviceTokenProvider.GetToken();
        return JwtForwardingClientInterceptor.WithAuthorization(
            context,
            string.IsNullOrEmpty(token) ? null : $"Bearer {token}");
    }
}
