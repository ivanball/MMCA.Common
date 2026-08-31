using System.Globalization;

namespace MMCA.Common.UI.Common;

/// <summary>
/// Route path constants for the Notification UI module.
/// </summary>
public static class NotificationRoutePaths
{
    public static readonly string Notifications = "/notifications";
    public static readonly string NotificationSend = "/notifications/send";
    public static readonly string NotificationInbox = "/notifications/inbox";

    /// <summary>
    /// Builds the typed deep link to one notification in the inbox
    /// (<c>/notifications/inbox/{Id:int}</c>). The route's <c>:int</c> constraint is the validation
    /// boundary, so this formats invariantly: a culture that renders digit groups or non-ASCII
    /// digits would produce a URL the constraint rejects.
    /// </summary>
    /// <param name="id">The user notification to open.</param>
    public static string NotificationInboxItem(UserNotificationIdentifierType id) =>
        string.Create(CultureInfo.InvariantCulture, $"{NotificationInbox}/{id}");
}
