using Microsoft.AspNetCore.Authorization;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// The requirement behind the policy the notification pages declare instead of a bare
/// <c>[Authorize]</c>. It asks for an authenticated caller exactly as <c>[Authorize]</c> does, except
/// when the host hides the notification pages
/// (<see cref="LayoutSettings.HideNotificationPagesWhenUnregistered"/> with no
/// <c>AddNotificationUI()</c>): then every caller passes, so the request reaches the router, whose
/// <see cref="NotificationPageGate"/> answers it with the not-found page (404).
/// </summary>
/// <remarks>
/// A bare <c>[Authorize]</c> becomes endpoint metadata, so on a Blazor Web host the authorization
/// middleware challenged a signed-out request (a redirect to the sign-in page) before the router's
/// gate ever ran, and a page that does not exist for this host asked the visitor to sign in.
/// Evaluated by <see cref="NotificationPageAuthorizationHandler"/>.
/// </remarks>
internal sealed class NotificationPageRequirement : IAuthorizationRequirement
{
    /// <summary>The policy name the notification pages reference.</summary>
    public const string PolicyName = "mmca:notification-pages";
}
