using Microsoft.AspNetCore.Http;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Host-level knobs for the HttpOnly session cookies written by <see cref="SessionCookieEndpoints"/>,
/// the server-side refresher and <see cref="ISessionCookieStore"/>. Not bound from configuration: the
/// defaults reproduce the cookie behavior every host has always had, and the one feature that changes
/// them (the same-origin API proxy in MMCA.Common.UI.Web) sets them in code when a host opts in.
/// </summary>
public sealed class SessionCookieSettings
{
    /// <summary>
    /// Gets or sets the <c>SameSite</c> attribute of both session cookies. Defaults to
    /// <see cref="SameSiteMode.Lax"/>; the same-origin API proxy raises it to
    /// <see cref="SameSiteMode.Strict"/> because the cookie then authenticates data calls, not only
    /// server-side rendering.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    /// <summary>
    /// Gets or sets a value indicating whether the browser may only ever see a claims-only token.
    /// When <see langword="true"/>, <c>POST /auth/session/token</c> answers with
    /// <see cref="SessionClaimsToken"/> (an unsigned copy of the access token's claims that no API
    /// accepts) instead of the real access token, and <c>POST /auth/session-cookie</c> ignores the
    /// tokens a browser posts (answering 204 without writing), because the server is then the only
    /// writer of the cookies. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ClaimsOnlyBrowserTokens { get; set; }

    /// <summary>
    /// Gets or sets the <c>Max-Age</c> of both session cookies. <see langword="null"/> (the default)
    /// derives it from <c>Jwt:RefreshTokenExpirationDays</c> when that section is bound in this host,
    /// and otherwise uses 7 days, so a cookie neither outlives nor undershoots the refresh token it
    /// carries. A UI host that does not bind <c>Jwt</c> and runs a non-default refresh lifetime sets
    /// it here.
    /// </summary>
    public TimeSpan? Lifetime { get; set; }
}
