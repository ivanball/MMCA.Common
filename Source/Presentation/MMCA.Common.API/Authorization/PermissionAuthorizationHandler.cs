using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;

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
    /// <summary>
    /// The one evaluation of "this principal holds this permission" every API-side check shares: an
    /// authenticated principal carrying the permission as a claim, or holding a role the registry
    /// grants it to (stored grants included, when the host layers them in).
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <param name="permissionRegistry">
    /// The role-to-permission registry, or <see langword="null"/> when the host registered none (the
    /// permission claim alone then decides).
    /// </param>
    /// <param name="permission">The permission.</param>
    /// <returns><see langword="true"/> when the principal holds <paramref name="permission"/>.</returns>
    internal static bool Holds(ClaimsPrincipal user, IPermissionRegistry? permissionRegistry, string permission) =>
        user.Identity?.IsAuthenticated == true
        && (user.HasPermissionClaim(permission)
            || permissionRegistry?.HasPermission(user.GetRoleValues(), permission) == true);

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (Holds(context.User, permissionRegistry, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
