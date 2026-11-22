using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// Withholds every role listed under <see cref="MultiFactorAuthorizationSettings.RequiredForRoles"/>, and
/// the permission claims that came with it, from a validated principal whose session carries no second
/// factor, and records what it withheld in <see cref="AuthClaimTypes.MultiFactorWithheldRole"/> and
/// <see cref="AuthClaimTypes.MultiFactorWithheldPermission"/> markers on the principal's identity.
/// </summary>
/// <remarks>
/// <para>
/// It changes the request's principal only, never the token, so every reader of the principal (role
/// checks, permission checks, the current-user service, the ambient origin of scheduled work) sees an
/// account without the listed role. The subject, email, tenant, session, sign-in time and every role
/// that is not listed stay as they were, so the account can still reach its own data, the two-factor
/// endpoints and sign-out.
/// </para>
/// <para>
/// A principal that is not authenticated, a machine client (<c>token_use=machine</c>), a session with
/// the <c>mfa</c> claim and a principal holding no listed role are left untouched, and with no listed
/// role the call returns before reading the principal. Running it twice changes nothing more: the
/// first run leaves no listed role behind.
/// </para>
/// </remarks>
internal static class MultiFactorRoleWithholding
{
    /// <summary>The role claim spellings <see cref="ClaimsPrincipalExtensions.GetRoleValues"/> reads.</summary>
    private static readonly string[] RoleClaimTypes = [ClaimTypes.Role, "role", "roles"];

    /// <summary>
    /// Applies the withholding with the role list and the permission registry the request's services
    /// hold.
    /// </summary>
    /// <param name="principal">The validated principal, changed in place.</param>
    /// <param name="services">The request's services, or <see langword="null"/> (nothing is withheld).</param>
    internal static void Apply(ClaimsPrincipal? principal, IServiceProvider? services)
    {
        if (principal is null
            || services?.GetService<IOptions<MultiFactorAuthorizationSettings>>()?.Value.RequiredForRoles is not { Count: > 0 } requiredForRoles)
        {
            return;
        }

        Apply(principal, requiredForRoles, services.GetService<IPermissionRegistry>());
    }

    /// <summary>Applies the withholding for <paramref name="requiredForRoles"/>.</summary>
    /// <param name="principal">The validated principal, changed in place.</param>
    /// <param name="requiredForRoles">The listed roles; blank entries are ignored, names match case-insensitively.</param>
    /// <param name="permissionRegistry">
    /// The role-to-permission registry that decides which permission claims the remaining roles keep, or
    /// <see langword="null"/> (no permission claim is kept).
    /// </param>
    internal static void Apply(
        ClaimsPrincipal principal,
        IEnumerable<string> requiredForRoles,
        IPermissionRegistry? permissionRegistry)
    {
        var listed = requiredForRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (listed.Count == 0 || !IsSubjectToTheRule(principal))
        {
            return;
        }

        var withheldRoles = RemoveClaims(principal, claim => IsRoleClaim(claim) && listed.Contains(claim.Value));
        if (withheldRoles.Count == 0)
        {
            return;
        }

        // The token carries one permission claim per permission its role grants, so the listed role's
        // power would stay in place through them. A permission the remaining roles grant is kept.
        var remainingRoles = principal.GetRoleValues().ToList();
        var withheldPermissions = RemoveClaims(principal, claim =>
            string.Equals(claim.Type, AuthClaimTypes.Permission, StringComparison.Ordinal)
            && !(remainingRoles.Count > 0 && permissionRegistry?.HasPermission(remainingRoles, claim.Value) == true));

        if (principal.Identity is ClaimsIdentity identity)
        {
            AddMarkers(identity, AuthClaimTypes.MultiFactorWithheldRole, withheldRoles.Distinct(StringComparer.OrdinalIgnoreCase));
            AddMarkers(identity, AuthClaimTypes.MultiFactorWithheldPermission, withheldPermissions.Distinct(StringComparer.Ordinal));
        }
    }

    // An authenticated person (not a machine client) whose session carries no second factor.
    private static bool IsSubjectToTheRule(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && !principal.HasClaim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine)
        && !principal.HasMultiFactor();

    private static bool IsRoleClaim(Claim claim) =>
        RoleClaimTypes.Contains(claim.Type, StringComparer.Ordinal);

    private static void AddMarkers(ClaimsIdentity identity, string markerType, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            identity.AddClaim(new Claim(markerType, value));
        }
    }

    // Removes every matching claim from every identity of the principal and returns the removed values.
    private static List<string> RemoveClaims(ClaimsPrincipal principal, Func<Claim, bool> match)
    {
        var removed = new List<string>();
        foreach (var identity in principal.Identities)
        {
            // Copied first: the identity's claim list cannot change while it is enumerated.
            var matching = identity.Claims.Where(match).ToList();
            removed.AddRange(matching.Where(identity.TryRemoveClaim).Select(claim => claim.Value));
        }

        return removed;
    }
}
