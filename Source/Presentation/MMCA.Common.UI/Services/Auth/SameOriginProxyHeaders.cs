namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// The fixed request header the same-origin API proxy (<c>AddCommonSameOriginApiProxy</c> in
/// MMCA.Common.UI.Web) requires on every state-changing request. A cross-site page can make a browser
/// send a simple POST with the user's cookies, but it cannot add a custom header without a CORS
/// preflight, and the proxy answers <c>OPTIONS</c> itself with no CORS grant (never forwarding it to
/// the gateway's CORS policy) and refuses any request whose <c>Origin</c> or <c>Sec-Fetch-Site</c> names
/// another origin. The header is defense in depth behind that origin check; the value carries no secret.
/// </summary>
public static class SameOriginProxyHeaders
{
    /// <summary>The CSRF header name.</summary>
    public const string CsrfHeaderName = "X-CSRF";

    /// <summary>The only accepted CSRF header value.</summary>
    public const string CsrfHeaderValue = "1";
}
