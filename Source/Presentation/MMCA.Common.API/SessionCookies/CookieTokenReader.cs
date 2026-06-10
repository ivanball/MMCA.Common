using Microsoft.AspNetCore.Http;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Reads the auth JWT and refresh token from the request cookies written by
/// <see cref="SessionCookieEndpoints"/>. Used by the server-side token storage
/// during SSR prerender, when JS interop (localStorage) is unreachable.
/// </summary>
public sealed class CookieTokenReader(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which <see cref="CookieSessionRefresher"/> stashes a
    /// freshly-refreshed access token for the current request, so SSR authentication uses the new token
    /// rather than the still-expired one in the request cookie.
    /// </summary>
    internal const string FreshAccessTokenItemKey = "mmca.fresh-access-token";

    public string? ReadAccessToken()
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        if (context.Items.TryGetValue(FreshAccessTokenItemKey, out var fresh) &&
            fresh is string freshToken && !string.IsNullOrWhiteSpace(freshToken))
        {
            return freshToken;
        }

        return context.Request.Cookies[SessionCookieEndpoints.AccessTokenCookieName];
    }

    public string? ReadRefreshToken() =>
        httpContextAccessor.HttpContext?.Request.Cookies[SessionCookieEndpoints.RefreshTokenCookieName];
}
