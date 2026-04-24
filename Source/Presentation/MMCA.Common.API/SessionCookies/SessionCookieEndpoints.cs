using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Maps endpoints that let the browser mirror its localStorage JWT into an HttpOnly cookie
/// on the UI host's origin. The cookie is read by <see cref="CookieTokenReader"/> during
/// SSR prerender so <c>[Authorize]</c> pages opened in a new tab don't redirect to /login.
/// </summary>
public static class SessionCookieEndpoints
{
    public const string AccessTokenCookieName = "mmca_auth_access";
    public const string RefreshTokenCookieName = "mmca_auth_refresh";

    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    public static IEndpointRouteBuilder MapSessionCookieEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/auth/session-cookie")
            .ExcludeFromDescription();

        group.MapPost(string.Empty, (SessionCookieRequest request, HttpContext httpContext, IWebHostEnvironment env) =>
        {
            var options = BuildCookieOptions(env, CookieLifetime);
            httpContext.Response.Cookies.Append(AccessTokenCookieName, request.AccessToken, options);
            httpContext.Response.Cookies.Append(RefreshTokenCookieName, request.RefreshToken, options);
            return Results.NoContent();
        }).DisableAntiforgery();

        group.MapDelete(string.Empty, (HttpContext httpContext, IWebHostEnvironment env) =>
        {
            var options = BuildCookieOptions(env, TimeSpan.Zero);
            httpContext.Response.Cookies.Delete(AccessTokenCookieName, options);
            httpContext.Response.Cookies.Delete(RefreshTokenCookieName, options);
            return Results.NoContent();
        }).DisableAntiforgery();

        return endpoints;
    }

    private static CookieOptions BuildCookieOptions(IWebHostEnvironment env, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = !env.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
    };

    public sealed record SessionCookieRequest(string AccessToken, string RefreshToken);
}
