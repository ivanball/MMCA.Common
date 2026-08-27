using System.Security.Claims;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Testing.UI;

/// <summary>Factory for <see cref="ClaimsPrincipal"/> instances used in bUnit component tests.</summary>
public static class TestPrincipal
{
    /// <summary>
    /// Builds an authenticated principal. The identity carries an authentication type (so
    /// <c>IsAuthenticated == true</c>), the user identifier (read by pages such as Identity's
    /// Profile), a name, and the supplied roles (matched by <c>&lt;AuthorizeView Roles="…"&gt;</c>).
    /// </summary>
    /// <remarks>
    /// The identifier is written twice, under <c>sub</c> and under
    /// <see cref="ClaimTypes.NameIdentifier"/>, because a real principal reaches a page under either
    /// name: a token read directly carries the raw <c>sub</c> the token service emits, while the JWT
    /// bearer handler maps it onto NameIdentifier. A page reading through
    /// <see cref="ClaimsPrincipalExtensions.FindUserIdValue"/> resolves both, and one written here
    /// keeps a page that still reads a single claim type working under this double.
    /// </remarks>
    public static ClaimsPrincipal AuthenticatedUser(string userId = "1", string name = "Test User", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new(AuthClaimTypes.Subject, userId),
            new(ClaimTypes.NameIdentifier, userId),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    /// <summary>An authenticated organizer/admin (carries the <c>Organizer</c> role).</summary>
    public static ClaimsPrincipal Organizer(string userId = "1")
        => AuthenticatedUser(userId, "Organizer User", "Organizer");
}
