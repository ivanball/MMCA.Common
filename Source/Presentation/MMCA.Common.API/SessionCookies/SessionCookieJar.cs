using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Single place that writes and clears the HttpOnly auth cookies, so the endpoints, the
/// server-side refresher, and the SSR middleware all use identical cookie options.
/// </summary>
internal static class SessionCookieJar
{
    // The refresh-token lifetime's default (JwtSettings.RefreshTokenExpirationDays), used when the
    // host configures neither the cookie lifetime nor the Jwt section.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    internal static void Append(HttpContext context, string accessToken, string refreshToken, IWebHostEnvironment environment) =>
        Append(context, accessToken, refreshToken, environment, SameSiteMode.Lax);

    internal static void Append(HttpContext context, string accessToken, string refreshToken, IWebHostEnvironment environment, SameSiteMode sameSite)
    {
        var options = BuildOptions(environment, ResolveLifetime(context), sameSite);
        context.Response.Cookies.Append(SessionCookieEndpoints.AccessTokenCookieName, accessToken, options);
        context.Response.Cookies.Append(SessionCookieEndpoints.RefreshTokenCookieName, refreshToken, options);
    }

    /// <summary>
    /// The cookies' <c>Max-Age</c>: <see cref="SessionCookieSettings.Lifetime"/> when the host set
    /// it, else the configured refresh-token lifetime, else 7 days. A cookie must not outlive the
    /// credential it carries, and one that expires first signs the user out early.
    /// </summary>
    internal static TimeSpan ResolveLifetime(HttpContext context)
    {
        // RequestServices is declared non-nullable but is null on a bare DefaultHttpContext.
        IServiceProvider? services = context.RequestServices;

        if (services?.GetService<IOptions<SessionCookieSettings>>()?.Value.Lifetime is { } configured)
        {
            return configured;
        }

        return services?.GetService<IOptions<JwtSettings>>()?.Value is { RefreshTokenExpirationDays: > 0 } jwt
            ? TimeSpan.FromDays(jwt.RefreshTokenExpirationDays)
            : DefaultLifetime;
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
