using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.UI.Authorization;

/// <summary>
/// Evaluates <see cref="PagePermissionRequirement"/> with <c>HasPermissionClaim</c>, the framework's
/// single definition of a token-granted permission and the same check the shared nav menu runs for
/// <c>NavItem.RequiredPermission</c>, so a page and the menu entry that opens it cannot disagree. The
/// policy also requires an authenticated user, so an unauthenticated principal never passes here.
/// </summary>
internal sealed class PagePermissionAuthorizationHandler : AuthorizationHandler<PagePermissionRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PagePermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.HasPermissionClaim(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
