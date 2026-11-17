using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Evaluates <see cref="NotificationPageRequirement"/>: an authenticated caller passes when it also
/// holds the requirement's permission (if any), and every caller passes while the host hides the
/// notification pages because it never registered the notification UI (the router then answers 404).
/// </summary>
/// <param name="modules">The UI modules the host registered.</param>
internal sealed class NotificationPageAuthorizationHandler(IEnumerable<IUIModule> modules)
    : AuthorizationHandler<NotificationPageRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NotificationPageRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (NotificationPageGate.HidesNotificationPages(modules)
            || IsSignedInWithPermission(context, requirement))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    // The same permission check the pages run themselves (HasPermissionClaim), so the policy and the
    // in-page gate cannot disagree.
    private static bool IsSignedInWithPermission(
        AuthorizationHandlerContext context,
        NotificationPageRequirement requirement) =>
        context.User.Identities.Any(identity => identity.IsAuthenticated)
        && (requirement.Permission is null || context.User.HasPermissionClaim(requirement.Permission));
}
