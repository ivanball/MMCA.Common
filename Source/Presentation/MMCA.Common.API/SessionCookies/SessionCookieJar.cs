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

    internal static void Append(HttpContext context, string accessToken, string refreshToken, IWebHostEnvironment environment)
    {
        var options = BuildOptions(environment, Lifetime);
        context.Response.Cookies.Append(SessionCookieEndpoints.AccessTokenCookieName, accessToken, options);
        context.Response.Cookies.Append(SessionCookieEndpoints.RefreshTokenCookieName, refreshToken, options);
    }

    internal static void Delete(HttpContext context, IWebHostEnvironment environment)
    {
        var options = BuildOptions(environment, TimeSpan.Zero);
        context.Response.Cookies.Delete(SessionCookieEndpoints.AccessTokenCookieName, options);
        context.Response.Cookies.Delete(SessionCookieEndpoints.RefreshTokenCookieName, options);
    }

    private static CookieOptions BuildOptions(IWebHostEnvironment environment, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime > TimeSpan.Zero ? lifetime : null,
    };
}
