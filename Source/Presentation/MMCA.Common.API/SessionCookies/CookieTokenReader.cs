using Microsoft.AspNetCore.Http;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Reads the auth JWT and refresh token from the request cookies written by
/// <see cref="SessionCookieEndpoints"/>. Used by the server-side token storage
/// during SSR prerender, when JS interop (localStorage) is unreachable.
/// </summary>
public sealed class CookieTokenReader(IHttpContextAccessor httpContextAccessor)
{
    public string? ReadAccessToken() =>
        httpContextAccessor.HttpContext?.Request.Cookies[SessionCookieEndpoints.AccessTokenCookieName];

    public string? ReadRefreshToken() =>
        httpContextAccessor.HttpContext?.Request.Cookies[SessionCookieEndpoints.RefreshTokenCookieName];
}
