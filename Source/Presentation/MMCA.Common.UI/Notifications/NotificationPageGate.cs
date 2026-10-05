using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Pages.Notifications;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Decides whether the router should answer a notification page with the not-found page because the
/// host opted in (<see cref="LayoutSettings.HideNotificationPagesWhenUnregistered"/>) and never
/// registered the notification UI.
/// </summary>
internal static class NotificationPageGate
{
    /// <summary>Every routable page that needs the services <c>AddNotificationUI()</c> registers.</summary>
    private static readonly Type[] NotificationPages = [typeof(NotificationList), typeof(NotificationInbox), typeof(NotificationSend)];

    /// <summary>Whether <paramref name="pageType"/> is hidden for this host.</summary>
    /// <param name="pageType">The page the router matched.</param>
    /// <param name="settings">The host's layout settings.</param>
    /// <param name="modules">The UI modules the host registered.</param>
    /// <returns><see langword="true"/> to render the not-found page instead of the matched page.</returns>
    public static bool Hides(Type pageType, LayoutSettings settings, IEnumerable<IUIModule> modules) =>
        NotificationPages.Contains(pageType) && HidesNotificationPages(settings, modules);

    /// <summary>Whether this host hides every notification page.</summary>
    /// <param name="settings">The host's layout settings.</param>
    /// <param name="modules">The UI modules the host registered.</param>
    /// <returns><see langword="true"/> when the notification pages answer with the not-found page.</returns>
    public static bool HidesNotificationPages(LayoutSettings settings, IEnumerable<IUIModule> modules) =>
        settings.HideNotificationPagesWhenUnregistered
        && !modules.Any(m => m is NotificationUIModule);
}
