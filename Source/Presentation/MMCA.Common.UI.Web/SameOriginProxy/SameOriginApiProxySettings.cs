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
///   "AdditionalTokenIssuingPaths": [ "auth/2fa/verify" ],
///   "MaxRequestBodySizeByPath": { "uploads/file": 52428800 }
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
    /// Gets the request body size limits, in bytes, that replace the server default (Kestrel's is
    /// 30,000,000 bytes) for specific endpoints. Each key is a path relative to the gateway root, shaped
    /// like <see cref="RefreshPath"/> (for example <c>uploads/file</c>, matched case-insensitively); it
    /// covers that path and every path below it on a whole-segment boundary, so <c>uploads/file</c>
    /// covers <c>uploads/file/5</c> but not <c>uploads/files</c>. Where several keys match, the longest
    /// wins. Every other path keeps the server default, and every limit must be positive. Empty by
    /// default.
    /// </summary>
    /// <remarks>
    /// Without an entry, an upload larger than the server default is refused 413 by this host before a
    /// byte reaches the gateway, whatever the API behind it accepts. Example (<c>appsettings.json</c>):
    /// <code>
    /// "SameOriginApiProxy": {
    ///   "MaxRequestBodySizeByPath": { "uploads/file": 52428800 }
    /// }
    /// </code>
    /// A key containing <c>/</c> cannot be set from an environment variable (the name has no way to
    /// carry the slash), so set these in a JSON configuration file or another provider that accepts
    /// arbitrary keys.
    /// </remarks>
    public IDictionary<string, long> MaxRequestBodySizeByPath { get; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

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
