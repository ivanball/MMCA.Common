namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// The fixed request header the same-origin API proxy (<c>AddCommonSameOriginApiProxy</c> in
/// MMCA.Common.UI.Web) requires on every state-changing request. A cross-site page can make a browser
/// send a simple POST with the user's cookies, but it cannot add a custom header without a CORS
/// preflight the proxy never grants, so the header's presence proves the request came from this
/// origin's own script. The value carries no secret.
/// </summary>
public static class SameOriginProxyHeaders
{
    /// <summary>The CSRF header name.</summary>
    public const string CsrfHeaderName = "X-CSRF";

    /// <summary>The only accepted CSRF header value.</summary>
    public const string CsrfHeaderValue = "1";
}
