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

        if (context.User.HasPermissionClaim(requirement.Permission)
            || permissionRegistry.HasPermission(context.User.GetRoleValues(), requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
