using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Maps the session-cookie endpoints. <c>/auth/session-cookie</c> (POST/DELETE) lets the browser seed or
/// clear the HttpOnly auth cookies from JS at login/logout; <c>/auth/session/token</c> (POST) is the
/// same-origin "validate-or-refresh" endpoint the browser polls to hydrate its in-memory access token —
/// the refresh token stays server-side. The cookies are read by <see cref="CookieTokenReader"/> during
/// SSR prerender so <c>[Authorize]</c> pages opened in a new tab / on F5 don't redirect to /login.
/// </summary>
public static class SessionCookieEndpoints
{
    public const string AccessTokenCookieName = "mmca_auth_access";
    public const string RefreshTokenCookieName = "mmca_auth_refresh";

    public static IEndpointRouteBuilder MapSessionCookieEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/auth/session-cookie")
            .ExcludeFromDescription();

        group.MapPost(string.Empty, (SessionCookieRequest request, HttpContext httpContext, IWebHostEnvironment env) =>
        {
            SessionCookieJar.Append(httpContext, request.AccessToken, request.RefreshToken, env);
            return Results.NoContent();
        }).DisableAntiforgery();

        group.MapDelete(string.Empty, (HttpContext httpContext, IWebHostEnvironment env) =>
        {
            SessionCookieJar.Delete(httpContext, env);
            return Results.NoContent();
        }).DisableAntiforgery();

        // Same-origin validate-or-refresh. The browser calls this (credentials:'same-origin') to hydrate
        // its in-memory access token from the HttpOnly cookies; the refresh token never leaves the server.
        // 401 (JSON) when there is no valid session. AllowAnonymous (it authenticates via the cookies),
        // antiforgery disabled (no token cookie), CSRF-guarded by POST + SameSite=Lax + Sec-Fetch-Site.
        endpoints.MapPost("/auth/session/token", async (
            HttpContext httpContext, ICookieSessionRefresher refresher, CancellationToken cancellationToken) =>
        {
            if (IsCrossSite(httpContext.Request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var result = await refresher.GetOrRefreshAsync(httpContext, cancellationToken).ConfigureAwait(false);
            return result is null
                ? Results.Json(new { error = "no_session" }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Json(new SessionTokenResponse(result.Value.AccessToken, result.Value.AccessTokenExpiry));
        })
        .ExcludeFromDescription()
        .AllowAnonymous()
        .DisableAntiforgery();

        return endpoints;
    }

    // Reject obvious cross-site POSTs (defense-in-depth alongside the cookie's SameSite=Lax, which already
    // blocks cross-site cookie attachment). Absent header → allow (older browsers).
    private static bool IsCrossSite(HttpRequest request) =>
        request.Headers.TryGetValue("Sec-Fetch-Site", out var site) &&
        string.Equals(site.ToString(), "cross-site", StringComparison.OrdinalIgnoreCase);

    public sealed record SessionCookieRequest(string AccessToken, string RefreshToken);
}
