using Microsoft.AspNetCore.Authorization;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// The requirement behind the policies the notification pages declare instead of a bare
/// <c>[Authorize]</c>. It asks for an authenticated caller exactly as <c>[Authorize]</c> does, plus
/// <see cref="Permission"/> when one is set, except when the host hides the notification pages
/// (<see cref="LayoutSettings.HideNotificationPagesWhenUnregistered"/> with no
/// <c>AddNotificationUI()</c>): then every caller passes, so the request reaches the router, whose
/// <see cref="NotificationPageGate"/> answers it with the not-found page (404).
/// </summary>
/// <remarks>
/// A bare <c>[Authorize]</c> becomes endpoint metadata, so on a Blazor Web host the authorization
/// middleware challenged a signed-out request (a redirect to the sign-in page) before the router's
/// gate ever ran, and a page that does not exist for this host asked the visitor to sign in.
/// The same metadata is what answers a full page load, so the history and compose pages carry the
/// <c>notifications:manage</c> permission here (<see cref="ManagePolicyName"/>): a signed-in caller
/// without it is refused at authorization (403, the in-shell Access Denied) instead of being served a
/// page that renders Forbidden under a 200. The inbox (<see cref="PolicyName"/>) stays open to every
/// signed-in caller. Evaluated by <see cref="NotificationPageAuthorizationHandler"/>.
/// </remarks>
/// <param name="permission">The permission the caller must also hold, or <see langword="null"/> for none.</param>
internal sealed class NotificationPageRequirement(string? permission = null) : IAuthorizationRequirement
{
    /// <summary>The policy name the inbox references: any signed-in caller.</summary>
    public const string PolicyName = "mmca:notification-pages";

    /// <summary>
    /// The policy name the history and compose pages reference: a signed-in caller holding
    /// <see cref="NotificationPermissions.Manage"/>.
    /// </summary>
    public const string ManagePolicyName = "mmca:notification-manage-pages";

    /// <summary>
    /// Gets the permission the caller must hold besides being signed in, or <see langword="null"/>
    /// when being signed in is enough.
    /// </summary>
    public string? Permission { get; } = permission;
}
