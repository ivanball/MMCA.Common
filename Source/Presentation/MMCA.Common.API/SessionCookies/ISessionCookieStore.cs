using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// Writes and clears the HttpOnly session cookies on a response, with the same options
/// (<c>HttpOnly</c>, <c>Secure</c> outside Development, <see cref="SessionCookieSettings.SameSite"/>,
/// 7-day lifetime) the session-cookie endpoints and the server-side refresher use. Registered by
/// <c>AddServerAuthSessionCookie</c>, for server components that obtain tokens themselves (the
/// same-origin API proxy) and must store them without a browser round trip.
/// </summary>
public interface ISessionCookieStore
{
    /// <summary>Appends both session cookies to the response.</summary>
    /// <param name="context">The request whose response carries the cookies.</param>
    /// <param name="accessToken">The access token.</param>
    /// <param name="refreshToken">The refresh token.</param>
    void Write(HttpContext context, string accessToken, string refreshToken);

    /// <summary>Expires both session cookies on the response.</summary>
    /// <param name="context">The request whose response clears the cookies.</param>
    void Clear(HttpContext context);
}

/// <summary>The <see cref="ISessionCookieStore"/> over <see cref="SessionCookieJar"/>.</summary>
internal sealed class SessionCookieStore(
    IWebHostEnvironment environment,
    IOptions<SessionCookieSettings> settings) : ISessionCookieStore
{
    public void Write(HttpContext context, string accessToken, string refreshToken) =>
        SessionCookieJar.Append(context, accessToken, refreshToken, environment, settings.Value.SameSite);

    public void Clear(HttpContext context) =>
        SessionCookieJar.Delete(context, environment, settings.Value.SameSite);
}
