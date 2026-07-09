namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete push notification settings bound from the <c>PushNotifications</c> configuration section.
/// </summary>
public sealed class PushNotificationSettings : IPushNotificationSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "PushNotifications";

    /// <inheritdoc />
    public bool Enabled { get; init; }

    /// <inheritdoc />
    public string HubPath { get; init; } = "/hubs/notifications";

    /// <summary>
    /// Gets the regular expression a channel key must match before a client may join or leave a
    /// channel via the notification hub. Guards SignalR group names against arbitrary client input.
    /// Deliberately declared on the concrete settings class only so that
    /// <see cref="IPushNotificationSettings"/> stays unchanged (no breaking change for implementers).
    /// </summary>
    public string ChannelKeyPattern { get; init; } = "^(event|session):[0-9]+$";
}
