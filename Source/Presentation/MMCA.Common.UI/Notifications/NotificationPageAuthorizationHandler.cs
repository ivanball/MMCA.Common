using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Evaluates <see cref="NotificationPageRequirement"/>: an authenticated caller passes, and so does
/// every caller while the host hides the notification pages (the router then answers 404).
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

        if (context.User.Identities.Any(identity => identity.IsAuthenticated)
            || NotificationPageGate.HidesNotificationPages(layoutOptions.Value, modules))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
