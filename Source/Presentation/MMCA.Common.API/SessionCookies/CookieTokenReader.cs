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

    /// <summary>
    /// Returns the access token the current request renders as: a live view-as overlay's token first
    /// (<see cref="ViewAsOverlayCookie"/>, ADR-137), else a token the refresher just minted, else the
    /// session access cookie.
    /// </summary>
    /// <returns>The access token, or <see langword="null"/>.</returns>
    public string? ReadAccessToken()
    {
        var context = httpContextAccessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        if (ViewAsOverlayCookie.TryRead(context.Request, TimeProvider.System.GetUtcNow(), out var viewAsToken, out _))
        {
            return viewAsToken;
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
