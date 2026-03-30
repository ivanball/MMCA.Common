using System.Reflection;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Notifications;
using MudBlazor;

namespace MMCA.Common.UI.Notifications;

/// <summary>
/// Notification module descriptor. Contributes navigation items for push notification
/// management and inbox, plus app-bar and layout components for real-time notifications.
/// </summary>
public sealed class NotificationUIModule : IUIModule
{
    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new("Notification Inbox", NotificationRoutePaths.NotificationInbox, Icons.Material.Filled.Inbox, Section: NavSection.User),
        new("Push Notifications", NotificationRoutePaths.Notifications, Icons.Material.Filled.NotificationsActive, RoleNames.Organizer, Section: NavSection.Admin, Group: "Notifications"),
    ];

    public IReadOnlyList<Type> AppBarComponentTypes { get; } = [typeof(NotificationBell)];

    public IReadOnlyList<Type> LayoutComponentTypes { get; } = [typeof(NotificationListener)];

    public Assembly Assembly => typeof(NotificationUIModule).Assembly;
}
