namespace MMCA.Common.Shared.Notifications.PushNotifications;

/// <summary>
/// Request record for sending a push notification to all recipients.
/// </summary>
public sealed record SendPushNotificationRequest(string Title, string Body)
{
    /// <summary>
    /// Gets the optional scope key the notification is stamped with (for example <c>"event:2"</c>).
    /// It is an init property rather than a positional parameter so every existing caller keeps
    /// compiling. Null (the default) sends an unscoped notification, visible to every read.
    /// </summary>
    public string? ScopeKey { get; init; }
}
