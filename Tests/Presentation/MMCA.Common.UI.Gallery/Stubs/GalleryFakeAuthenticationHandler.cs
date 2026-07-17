using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Cookie-toggled fake authentication for the backend-less gallery. A request carrying the
/// <c>gallery_auth=1</c> cookie authenticates as a fixed "Gallery Visitor" principal; every other
/// request stays anonymous. This exists because the shared notification pages carry a real
/// <c>[Authorize]</c> (rubric §25), which surfaces as endpoint metadata on the Razor Components
/// endpoints: without an authentication scheme the authorization middleware throws, and without an
/// authenticated principal the pages redirect to <c>/login</c> instead of rendering for the axe
/// scan. The signed-out pages (<c>/login</c>, <c>/register</c>, <c>/components</c>) are scanned
/// WITHOUT the cookie, so their chrome stays in the deliberate anonymous state. Test infrastructure
/// only; never copy into a real host.
/// </summary>
internal sealed class GalleryFakeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "GalleryFake";
    internal const string CookieName = "gallery_auth";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Cookies[CookieName] != "1")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Gallery Visitor")],
            SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
