using System.Security.Claims;

namespace MMCA.Common.Testing.UI;

/// <summary>Factory for <see cref="ClaimsPrincipal"/> instances used in bUnit component tests.</summary>
public static class TestPrincipal
{
    /// <summary>
    /// Builds an authenticated principal. The identity carries an authentication type (so
    /// <c>IsAuthenticated == true</c>), a <c>user_id</c> claim (read by pages such as Identity's
    /// Profile), a name, and the supplied roles (matched by <c>&lt;AuthorizeView Roles="…"&gt;</c>).
    /// </summary>
    public static ClaimsPrincipal AuthenticatedUser(string userId = "1", string name = "Test User", params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new("user_id", userId),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    /// <summary>An authenticated organizer/admin (carries the <c>Organizer</c> role).</summary>
    public static ClaimsPrincipal Organizer(string userId = "1")
        => AuthenticatedUser(userId, "Organizer User", "Organizer");
}
