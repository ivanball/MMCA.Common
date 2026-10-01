using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Single place that writes and clears the HttpOnly auth cookies, so the endpoints, the
/// server-side refresher, and the SSR middleware all use identical cookie options.
/// </summary>
internal static class SessionCookieJar
{
    // Aligned to the refresh-token lifetime (7 days) so a cookie never outlives the credential it carries.
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    internal static void Append(HttpContext context, string accessToken, string refreshToken, IWebHostEnvironment environment) =>
        Append(context, accessToken, refreshToken, environment, SameSiteMode.Lax);

    internal static void Append(HttpContext context, string accessToken, string refreshToken, IWebHostEnvironment environment, SameSiteMode sameSite)
    {
        var options = BuildOptions(environment, Lifetime, sameSite);
        context.Response.Cookies.Append(SessionCookieEndpoints.AccessTokenCookieName, accessToken, options);
        context.Response.Cookies.Append(SessionCookieEndpoints.RefreshTokenCookieName, refreshToken, options);
    }

    internal static void Delete(HttpContext context, IWebHostEnvironment environment) =>
        Delete(context, environment, SameSiteMode.Lax);

    internal static void Delete(HttpContext context, IWebHostEnvironment environment, SameSiteMode sameSite)
    {
        var options = BuildOptions(environment, TimeSpan.Zero, sameSite);
        context.Response.Cookies.Delete(SessionCookieEndpoints.AccessTokenCookieName, options);
        context.Response.Cookies.Delete(SessionCookieEndpoints.RefreshTokenCookieName, options);
    }

#pragma warning disable S2092 // Secure is conditional on the hosting environment (false only in Development, so http://localhost dev still works); it is true for every non-Development environment
    private static CookieOptions BuildOptions(IWebHostEnvironment environment, TimeSpan lifetime, SameSiteMode sameSite) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = sameSite,
        Path = "/",
        MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
    };
#pragma warning restore S2092
}
