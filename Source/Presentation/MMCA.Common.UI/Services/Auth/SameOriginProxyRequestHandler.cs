namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Innermost handler of the <c>"APIClient"</c> pipeline on a WebAssembly client whose host runs the
/// same-origin API proxy (<see cref="Common.Settings.ApiSettings.SameOriginApiEndpoint"/> set). It
/// removes any <c>Authorization</c> header the outer handlers or a service attached (the client only
/// ever holds a claims-only token, and the proxy attaches the real bearer from the HttpOnly session
/// cookie server-side) and stamps the proxy's CSRF header. Registered by <c>AddUIShared</c> only when
/// that setting is present, so every other client keeps its pipeline unchanged.
/// </summary>
internal sealed class SameOriginProxyRequestHandler : DelegatingHandler
{
    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Authorization = null;
        request.Headers.Remove(SameOriginProxyHeaders.CsrfHeaderName);
        request.Headers.TryAddWithoutValidation(SameOriginProxyHeaders.CsrfHeaderName, SameOriginProxyHeaders.CsrfHeaderValue);
        return base.SendAsync(request, cancellationToken);
    }
}
