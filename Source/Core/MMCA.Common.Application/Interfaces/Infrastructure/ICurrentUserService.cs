using System.Security.Claims;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Provides access to the currently authenticated user's identity and claims,
/// extracted from the HTTP context's JWT token.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>Gets the current user's claims principal.</summary>
    ClaimsPrincipal User { get; }

    /// <summary>Gets the current user's identifier, or <see langword="null"/> if unauthenticated.</summary>
    UserIdentifierType? UserId { get; }

    /// <summary>Gets the current user's role (e.g. "Organizer", "Attendee"), or <see langword="null"/> if unauthenticated.</summary>
    /// <remarks>
    /// This is the <b>first</b> role claim only. Use <see cref="Roles"/> or <see cref="IsInRole"/>
    /// for membership checks so a principal carrying more than one role is handled correctly.
    /// </remarks>
    string? Role { get; }

    /// <summary>
    /// Gets every role the current user holds, empty when unauthenticated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads all role claims rather than the first, and accepts each of the claim types the JWT
    /// middleware may produce: the standard <see cref="ClaimTypes.Role"/> URI when inbound claim
    /// mapping is on, or the raw <c>role</c> / <c>roles</c> claim when it is off.
    /// </para>
    /// <para>
    /// Falls back to <see cref="Role"/> when the principal yields no role claims, and null-guards
    /// <see cref="User"/> even though it is declared non-nullable. Both accommodate the fact that
    /// this is a default interface member: it runs against <em>every</em> implementation, not only
    /// the one shipped here. An implementation that populates <see cref="Role"/> but not a full
    /// principal (a hand-written double, or a mock stubbing only the properties a test needs) is
    /// common, and reading claims alone would have silently reported no roles for it, turning an
    /// authorization check into a denial; dereferencing a null principal would have turned it into
    /// a <see cref="NullReferenceException"/>. Claims win when present, so a multi-role principal
    /// is still read in full.
    /// </para>
    /// </remarks>
    IEnumerable<string> Roles
    {
        get
        {
            var claimed = User?.Claims
                .Where(claim =>
                    string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal)
                    || string.Equals(claim.Type, "role", StringComparison.Ordinal)
                    || string.Equals(claim.Type, "roles", StringComparison.Ordinal))
                .Select(claim => claim.Value)
                .ToArray() ?? [];

            if (claimed.Length > 0)
            {
                return claimed;
            }

            return Role is null ? [] : [Role];
        }
    }

    /// <summary>
    /// Extracts a typed claim value from the current user's claims.
    /// Useful for module-specific claims (e.g. speaker_id) without coupling Common to specific modules.
    /// </summary>
    /// <typeparam name="T">The target value type that implements <see cref="IParsable{TSelf}"/>.</typeparam>
    /// <param name="claimType">The claim type name to look up.</param>
    /// <returns>The parsed value, or <see langword="null"/> if the claim is missing or unparseable.</returns>
    T? GetClaimValue<T>(string claimType)
        where T : struct, IParsable<T>;

    /// <summary>
    /// Returns <see langword="true"/> if the current user holds <paramref name="roleName"/>,
    /// using case-insensitive comparison.
    /// </summary>
    /// <param name="roleName">The role name to check (use <see cref="Common.Shared.Auth.RoleNames"/> constants).</param>
    /// <returns><see langword="true"/> if the user has the specified role; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// Checks every role claim. Comparing against <see cref="Role"/> alone matched only the first
    /// one, so a principal holding several roles failed the check for all but whichever happened to
    /// be listed first. That is latent today (tokens carry a single role) and would have surfaced
    /// silently, as an authorization denial, the moment a second was added.
    /// </remarks>
    bool IsInRole(string roleName) =>
        Roles.Any(role => string.Equals(role, roleName, StringComparison.OrdinalIgnoreCase));
}
