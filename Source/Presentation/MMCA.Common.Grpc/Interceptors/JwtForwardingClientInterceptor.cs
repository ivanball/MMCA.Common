using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Grpc.Authentication;

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
/// <para>
/// When the inbound request carries no <c>Authorization</c> header but authenticated with a bearer
/// token taken from elsewhere (a SignalR hub connection passes it as <c>?access_token=</c>), the
/// token the request actually authenticated with is forwarded instead, read from the authentication
/// ticket (<c>JwtBearerOptions.SaveToken</c>, on by default, stores it as <c>access_token</c>). A
/// header that is present always wins.
/// </para>
/// <para>
/// When the call has no user token at all (no <see cref="HttpContext"/>, or a request that carried
/// none) and an <see cref="IServiceTokenProvider"/> is registered (<c>AddServiceTokenClient</c>), the
/// call is sent as <c>Bearer {serviceToken}</c> instead, so a background processor or an anonymous
/// request that fans out internally still authenticates to the downstream service as this service.
/// Precedence is therefore: the user's token, then the service token, then nothing. With no provider
/// registered the interceptor attaches nothing to such a call, exactly as before service tokens
/// existed.
/// </para>
/// <para>
/// An <c>Authorization</c> entry the caller already set on the call's metadata is never overwritten
/// or duplicated, whichever token would otherwise have been attached.
/// </para>
/// </summary>
/// <param name="httpContextAccessor">Accessor for the ambient request whose user token is forwarded.</param>
/// <param name="serviceTokenProvider">
/// The service-token source used when there is no user token, or <see langword="null"/> (the default
/// when <c>AddServiceTokenClient</c> was not called) to attach nothing in that case.
/// </param>
public sealed class JwtForwardingClientInterceptor(
    IHttpContextAccessor httpContextAccessor,
    IServiceTokenProvider? serviceTokenProvider = null) : Interceptor
{
    private const string AuthorizationHeader = "Authorization";

    private const string AccessTokenName = "access_token";

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
        // Never overwrite or duplicate a header a previous interceptor or the caller already set.
        // Checked first, so a caller-chosen credential never costs a service-token mint.
        if (context.Options.Headers?.GetValue(AuthorizationHeader) is not null)
        {
            return context;
        }

        var authHeader = ResolveAuthorization(httpContextAccessor.HttpContext) ?? ResolveServiceAuthorization();
        if (string.IsNullOrEmpty(authHeader))
        {
            return context;
        }

        var headers = context.Options.Headers ?? [];
        headers.Add(AuthorizationHeader, authHeader);
        var newOptions = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
    }

    /// <summary>
    /// The value to forward: the inbound <c>Authorization</c> header, or, when that is empty, a bearer
    /// built from the access token saved on the request's authentication ticket.
    /// </summary>
    /// <param name="httpContext">The ambient request, if any.</param>
    /// <returns>The <c>Authorization</c> value to forward, or <see langword="null"/>.</returns>
    private static string? ResolveAuthorization(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var header = httpContext.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(header))
        {
            return header;
        }

        var savedToken = httpContext.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties
            ?.GetTokenValue(AccessTokenName);
        return string.IsNullOrEmpty(savedToken) ? null : $"Bearer {savedToken}";
    }

    /// <summary>
    /// The service token as a bearer value, consulted only when there is no user token to forward.
    /// </summary>
    /// <returns>The <c>Authorization</c> value, or <see langword="null"/> when no provider is registered.</returns>
    private string? ResolveServiceAuthorization()
    {
        var token = serviceTokenProvider?.GetToken();
        return string.IsNullOrEmpty(token) ? null : $"Bearer {token}";
    }
}
