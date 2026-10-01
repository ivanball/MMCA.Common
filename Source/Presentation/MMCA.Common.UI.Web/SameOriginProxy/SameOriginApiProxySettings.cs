using Microsoft.AspNetCore.Http;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// Strongly-typed options bound to the <c>"SameOriginApiProxy"</c> configuration section, read by
/// <c>AddCommonSameOriginApiProxy</c>. Every value has a default, so a host opts in with no section at
/// all; the section exists to move the path or point at a different upstream.
/// </summary>
/// <remarks>
/// Example (<c>appsettings.json</c>):
/// <code>
/// "SameOriginApiProxy": {
///   "PathPrefix": "/api",
///   "GatewayAddress": "https+http://gateway",
///   "AdditionalTokenIssuingPaths": [ "auth/2fa/verify" ]
/// }
/// </code>
/// Validated at startup (<c>ValidateOnStart</c>); an invalid value fails the boot with a message naming it.
/// </remarks>
public sealed class SameOriginApiProxySettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public const string SectionName = "SameOriginApiProxy";

    /// <summary>
    /// The token-issuing endpoints every MMCA identity API exposes (relative to the gateway root): their
    /// successful responses carry a token pair, which the proxy moves into the HttpOnly session cookies
    /// before the body reaches the browser.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTokenIssuingPaths = ["auth/login", "auth/register", "auth/oauth/exchange"];

    /// <summary>
    /// Gets or sets the same-origin path the proxy serves (default <c>/api</c>). A request to
    /// <c>{PathPrefix}/orders/5</c> is forwarded to <c>{gateway}/orders/5</c>. Must start with <c>/</c>,
    /// must not end with one, and must be a literal path (no route syntax, query or fragment).
    /// </summary>
    public string PathPrefix { get; set; } = "/api";

    /// <summary>
    /// Gets or sets the upstream gateway base address. When unset (the default) the proxy uses
    /// <c>Api:ApiEndpoint</c>, the same server-side address the host's session refresher and API
    /// clients already call, so an Aspire service-discovery name such as <c>https+http://gateway</c>
    /// resolves exactly as it does for them.
    /// </summary>
    public string? GatewayAddress { get; set; }

    /// <summary>
    /// Gets the token-issuing endpoints a host adds to <see cref="DefaultTokenIssuingPaths"/> (relative
    /// to the gateway root, for example <c>auth/2fa/verify</c>). Empty by default.
    /// </summary>
    public IList<string> AdditionalTokenIssuingPaths { get; } = [];

    /// <summary>
    /// Gets or sets the refresh endpoint relative to the gateway root (default <c>auth/refresh</c>).
    /// A browser POST to <c>{PathPrefix}/{RefreshPath}</c> is answered by the proxy itself from the
    /// refresh cookie; the browser never posts a refresh token.
    /// </summary>
    public string RefreshPath { get; set; } = "auth/refresh";

    /// <summary>
    /// Gets or sets the sign-out endpoint relative to the gateway root (default <c>auth/revoke</c>). A
    /// browser POST to it is forwarded with the session's bearer and then clears the session cookies,
    /// whatever the upstream answered.
    /// </summary>
    public string RevokePath { get; set; } = "auth/revoke";

    /// <summary>
    /// Gets or sets the <c>SameSite</c> attribute of the session cookies while the proxy is enabled.
    /// Defaults to <see cref="SameSiteMode.Strict"/>; <see cref="SameSiteMode.Lax"/> is the only other
    /// accepted value, for a host that would rather keep server-rendered pages signed in when a
    /// signed-in user arrives from another site (a mailed link, a search result).
    /// </summary>
    public SameSiteMode SessionCookieSameSite { get; set; } = SameSiteMode.Strict;
}
