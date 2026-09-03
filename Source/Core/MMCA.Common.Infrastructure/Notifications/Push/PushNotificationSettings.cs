using MMCA.Common.Shared.Notifications;

namespace MMCA.Common.Infrastructure.Notifications.Push;

/// <summary>
/// Push notification settings bound from the <c>PushNotifications</c> configuration section.
/// </summary>
public sealed class PushNotificationSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "PushNotifications";

    /// <summary>Gets a value indicating whether push notifications are enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets the SignalR hub endpoint path.</summary>
    public string HubPath { get; init; } = "/hubs/notifications";

    /// <summary>
    /// Gets the regular expression a channel key must match before a client may join or leave a
    /// channel via the notification hub. Guards SignalR group names against arbitrary client input.
    /// <para>
    /// The default is <see cref="NotificationScopeKey.Pattern"/>, the same constant
    /// <see cref="NotificationScopeKey.ForEvent"/> and <see cref="NotificationScopeKey.ForSession"/>
    /// format against, so the producer and the guard cannot drift apart. A host that overrides the
    /// pattern from configuration takes on that alignment itself.
    /// </para>
    /// </summary>
    public string ChannelKeyPattern { get; init; } = NotificationScopeKey.Pattern;
}
