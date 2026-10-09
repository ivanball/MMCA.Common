using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Evaluates <see cref="NotificationPageRequirement"/>: an authenticated caller passes when it also
/// holds the requirement's permission (if any), and every caller passes while the host hides the
/// notification pages (the router then answers 404).
/// </summary>
/// <param name="layoutOptions">The host's layout settings.</param>
/// <param name="modules">The UI modules the host registered.</param>
internal sealed class NotificationPageAuthorizationHandler(
    IOptions<LayoutSettings> layoutOptions,
    IEnumerable<IUIModule> modules)
    : AuthorizationHandler<NotificationPageRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NotificationPageRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (NotificationPageGate.HidesNotificationPages(layoutOptions.Value, modules)
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
