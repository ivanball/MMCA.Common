using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace MMCA.Common.Infrastructure.Http;

/// <summary>
/// <see cref="DelegatingHandler"/> that copies the inbound <c>Authorization</c> header from
/// the current <see cref="HttpContext"/> onto every outgoing HTTP request. Used by typed
/// service clients to forward the caller's JWT bearer token to downstream services so
/// distributed authorization works without each handler having to thread the token through.
/// <para>
/// Mirrors the behavior of <c>JwtForwardingClientInterceptor</c> in <c>MMCA.Common.Grpc</c>.
/// The handler is a no-op when no <see cref="HttpContext"/> is available (e.g. background
/// processors invoking HTTP clients outside an HTTP request).
/// </para>
/// </summary>
public sealed class JwtForwardingDelegatingHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    private const string BearerScheme = "Bearer";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Skip if a previous handler or the caller already set Authorization explicitly.
        if (request.Headers.Authorization is not null)
        {
            return base.SendAsync(request, cancellationToken);
        }

        var inboundAuth = httpContextAccessor.HttpContext?.Request?.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(inboundAuth))
        {
            return base.SendAsync(request, cancellationToken);
        }

        // The inbound header is typically "Bearer <token>"; preserve it verbatim if so,
        // otherwise treat the entire string as the parameter under the Bearer scheme.
        const string bearerPrefix = BearerScheme + " ";
        var token = inboundAuth.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? inboundAuth[bearerPrefix.Length..]
            : inboundAuth;

        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, token);
        return base.SendAsync(request, cancellationToken);
    }
}
