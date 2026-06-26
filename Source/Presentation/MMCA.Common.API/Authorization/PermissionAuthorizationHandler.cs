using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Grants a <see cref="PermissionRequirement"/> when the authenticated principal holds the
/// permission directly (an <see cref="AuthClaimTypes.Permission"/> claim) or via one of its
/// roles, as defined by the registered <see cref="IPermissionRegistry"/>.
/// </summary>
/// <param name="permissionRegistry">The role-to-permission registry.</param>
public sealed class PermissionAuthorizationHandler(IPermissionRegistry permissionRegistry)
    : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        if (context.User.HasClaim(AuthClaimTypes.Permission, requirement.Permission)
            || permissionRegistry.HasPermission(GetRoles(context.User), requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    // Gather roles regardless of how the JWT middleware mapped the role claim type: the standard
    // ClaimTypes.Role URI, or the raw "role"/"roles" claim when inbound-claim mapping is disabled.
    private static IEnumerable<string> GetRoles(ClaimsPrincipal user) =>
        user.Claims
            .Where(claim =>
                string.Equals(claim.Type, ClaimTypes.Role, StringComparison.Ordinal)
                || string.Equals(claim.Type, "role", StringComparison.Ordinal)
                || string.Equals(claim.Type, "roles", StringComparison.Ordinal))
            .Select(claim => claim.Value);
}
