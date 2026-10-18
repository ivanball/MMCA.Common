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
/// This interceptor forwards only the user's token. A call with no user token goes out with no
/// <c>Authorization</c> entry unless its client opted into service tokens with
/// <see cref="ServiceTokenHttpClientBuilderExtensions"/>' <c>WithServiceToken()</c>, which adds
/// <see cref="ServiceTokenClientInterceptor"/> for that client alone.
/// </para>
/// <para>
/// An <c>Authorization</c> entry the caller already set on the call's metadata is never overwritten
/// or duplicated.
/// </para>
/// </summary>
/// <param name="httpContextAccessor">Accessor for the ambient request whose user token is forwarded.</param>
public sealed class JwtForwardingClientInterceptor(IHttpContextAccessor httpContextAccessor) : Interceptor
{
    internal const string AuthorizationHeader = "Authorization";

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

    /// <summary>
    /// Returns <paramref name="context"/> with <c>Authorization: {authHeader}</c> added, unless the
    /// call already carries an <c>Authorization</c> entry (never overwritten or duplicated) or
    /// <paramref name="authHeader"/> is empty.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TResponse">The response message type.</typeparam>
    /// <param name="context">The outgoing call's context.</param>
    /// <param name="authHeader">The value to attach, or <see langword="null"/> to attach nothing.</param>
    /// <returns>The context to continue the call with.</returns>
    internal static ClientInterceptorContext<TRequest, TResponse> WithAuthorization<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        string? authHeader)
        where TRequest : class
        where TResponse : class
    {
        if (string.IsNullOrEmpty(authHeader) || HasAuthorization(context))
        {
            return context;
        }

        var headers = context.Options.Headers ?? [];
        headers.Add(AuthorizationHeader, authHeader);
        var newOptions = context.Options.WithHeaders(headers);
        return new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, newOptions);
    }

    /// <summary>
    /// Whether the call already carries an <c>Authorization</c> entry, set by the caller or by an
    /// earlier interceptor.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TResponse">The response message type.</typeparam>
    /// <param name="context">The outgoing call's context.</param>
    /// <returns><see langword="true"/> when an <c>Authorization</c> entry is present.</returns>
    internal static bool HasAuthorization<TRequest, TResponse>(ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class =>
        context.Options.Headers?.GetValue(AuthorizationHeader) is not null;

    /// <summary>
    /// The user's token to forward: the inbound <c>Authorization</c> header, or, when that is empty, a
    /// bearer built from the access token saved on the request's authentication ticket.
    /// </summary>
    /// <param name="httpContext">The ambient request, if any.</param>
    /// <returns>The <c>Authorization</c> value to forward, or <see langword="null"/>.</returns>
    internal static string? ResolveUserAuthorization(HttpContext? httpContext)
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

    private ClientInterceptorContext<TRequest, TResponse> WithForwardedAuthorization<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class =>
        HasAuthorization(context)
            ? context
            : WithAuthorization(context, ResolveUserAuthorization(httpContextAccessor.HttpContext));
}
