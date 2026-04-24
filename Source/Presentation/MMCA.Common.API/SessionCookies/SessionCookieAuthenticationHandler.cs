using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MMCA.Common.API.SessionCookies;

/// <summary>
/// ASP.NET Core authentication scheme that reads the JWT from the session cookie
/// (<c>mmca_auth_access</c>), parses its claims, and populates <see cref="HttpContext.User"/>
/// during SSR prerender. Enables both Blazor Web App's internal SSR authorization flow and
/// endpoint-level <c>[Authorize]</c> enforcement to pass on fresh GETs (right-click → new tab,
/// F5, external deep link) before the interactive phase starts.
/// </summary>
/// <remarks>
/// Signature is not validated here — the cookie was minted by the UI host in response to a
/// successful login against the API, and the API still performs full JWT validation on every
/// API call. This handler only extracts claims so ASP.NET Core's auth system can read the
/// user's identity.
/// </remarks>
public sealed class SessionCookieAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CookieTokenReader cookieTokenReader)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The canonical scheme name; use when registering and referencing this handler.</summary>
    public const string SchemeName = "SessionCookie";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = cookieTokenReader.ReadAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        try
        {
            var jwtHandler = new JwtSecurityTokenHandler();
            if (!jwtHandler.CanReadToken(token))
            {
                return Task.FromResult(AuthenticateResult.Fail("Session cookie is not a valid JWT."));
            }

            var jwt = jwtHandler.ReadJwtToken(token);
            if (jwt.ValidTo < DateTime.UtcNow)
            {
                return Task.FromResult(AuthenticateResult.Fail("Session cookie JWT is expired."));
            }

            var identity = new ClaimsIdentity(jwt.Claims, Scheme.Name, ClaimTypes.NameIdentifier, ClaimTypes.Role);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex));
        }
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = Request.Path + Request.QueryString;
        Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Registration extensions for <see cref="SessionCookieAuthenticationHandler"/>.
/// </summary>
public static class SessionCookieAuthenticationExtensions
{
    extension(AuthenticationBuilder builder)
    {
        /// <summary>
        /// Registers the session-cookie authentication scheme. Use after
        /// <c>AddAuthentication(SessionCookieAuthenticationHandler.SchemeName)</c>.
        /// </summary>
        public AuthenticationBuilder AddSessionCookieAuthentication() =>
            builder.AddScheme<AuthenticationSchemeOptions, SessionCookieAuthenticationHandler>(
                SessionCookieAuthenticationHandler.SchemeName, displayName: null, configureOptions: null);
    }
}
