using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// The view-as overlay cookie (ADR-137): a separate, short-lived HttpOnly cookie that carries a
/// read-only view-as access token on a web host with the same-origin API proxy, so the staff user's
/// own session cookies survive the view untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the proxy writes it</b>, from a successful view-as grant response, and only the proxy's
/// end-view-as and sign-out paths (or an expired or refused token) delete it. It holds an access token
/// and nothing to renew it with: the refresh path never reads it, so a view-as token is never renewed.
/// </para>
/// <para>
/// <b>While it holds an unexpired token it wins</b> over the session access token everywhere the
/// browser's identity is read: the proxy's forwarded bearer, <see cref="CookieTokenReader"/> (server
/// rendering), <c>POST /auth/session/token</c> and the Blazor Server handoff. An expired one is
/// ignored, so the browser falls back to the staff user's own session.
/// </para>
/// </remarks>
public static class ViewAsOverlayCookie
{
    /// <summary>The cookie name.</summary>
    public const string Name = "mmca_view_as";

    /// <summary>Whether the request carries the cookie at all, expired or not.</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when the cookie is present.</returns>
    public static bool IsPresent(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return !string.IsNullOrEmpty(request.Cookies[Name]);
    }

    /// <summary>
    /// Reads the overlay's token when it is a well-formed JWT that has not expired at
    /// <paramref name="now"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="token">The view-as access token.</param>
    /// <param name="expiresAt">The token's UTC expiry.</param>
    /// <returns><see langword="true"/> when a usable overlay is present.</returns>
    public static bool TryRead(HttpRequest request, DateTimeOffset now, [NotNullWhen(true)] out string? token, out DateTime expiresAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        token = null;
        expiresAt = default;

        var value = request.Cookies[Name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(value))
        {
            return false;
        }

        try
        {
            var validTo = handler.ReadJwtToken(value).ValidTo;
            if (validTo <= now.UtcDateTime)
            {
                return false;
            }

            token = value;
            expiresAt = validTo;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Writes the overlay, living exactly as long as the token it carries.</summary>
    /// <param name="context">The request whose response carries the cookie.</param>
    /// <param name="token">The view-as access token.</param>
    /// <param name="expiresAt">The token's UTC expiry.</param>
    /// <param name="sameSite">The <c>SameSite</c> mode (the session cookies' own).</param>
    public static void Write(HttpContext context, string token, DateTime expiresAt, SameSiteMode sameSite)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var lifetime = expiresAt - DateTime.UtcNow;
        if (lifetime <= TimeSpan.Zero)
        {
            return;
        }

        context.Response.Cookies.Append(Name, token, BuildOptions(context, sameSite, lifetime));
    }

    /// <summary>Expires the overlay on the response.</summary>
    /// <param name="context">The request whose response clears the cookie.</param>
    /// <param name="sameSite">The <c>SameSite</c> mode it was written with.</param>
    public static void Delete(HttpContext context, SameSiteMode sameSite)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Cookies.Delete(Name, BuildOptions(context, sameSite, lifetime: null));
    }

    // The session cookies' rule (SessionCookieJar): Secure outside Development whatever the request
    // scheme, and in Development whenever the request itself is HTTPS.
#pragma warning disable S2092 // Secure is false only for a Development request that arrived over plain http, as for the session cookies
    private static CookieOptions BuildOptions(HttpContext context, SameSiteMode sameSite, TimeSpan? lifetime)
    {
        // RequestServices is declared non-nullable but is null on a bare DefaultHttpContext.
        IServiceProvider? services = context.RequestServices;
        var development = services?.GetService<IWebHostEnvironment>()?.IsDevelopment() == true;

        return new CookieOptions
        {
            HttpOnly = true,
            Secure = !development || context.Request.IsHttps,
            SameSite = sameSite,
            Path = "/",
            MaxAge = lifetime,
        };
    }
#pragma warning restore S2092
}
