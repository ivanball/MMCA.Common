using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Notifications;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Decides whether the router should answer a notification page with the not-found page because the
/// host never registered the notification UI (no <c>AddNotificationUI()</c>, so no
/// <see cref="NotificationUIModule"/>): those pages need services such a host does not have.
/// </summary>
internal static class NotificationPageGate
{
    /// <summary>Every routable page that needs the services <c>AddNotificationUI()</c> registers.</summary>
    private static readonly Type[] NotificationPages = [typeof(NotificationList), typeof(NotificationInbox), typeof(NotificationSend)];

    /// <summary>Whether <paramref name="pageType"/> is hidden for this host.</summary>
    /// <param name="pageType">The page the router matched.</param>
    /// <param name="modules">The UI modules the host registered.</param>
    /// <returns><see langword="true"/> to render the not-found page instead of the matched page.</returns>
    public static bool Hides(Type pageType, IEnumerable<IUIModule> modules) =>
        NotificationPages.Contains(pageType) && HidesNotificationPages(modules);

    /// <summary>Whether this host hides every notification page.</summary>
    /// <param name="modules">The UI modules the host registered.</param>
    /// <returns><see langword="true"/> when the notification pages answer with the not-found page.</returns>
    public static bool HidesNotificationPages(IEnumerable<IUIModule> modules) =>
        !modules.Any(m => m is NotificationUIModule);
}
