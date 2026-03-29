namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Push notification configuration. Bound from the <c>PushNotifications</c> configuration section.
/// </summary>
public interface IPushNotificationSettings
{
    /// <summary>Gets a value indicating whether push notifications are enabled.</summary>
    bool Enabled { get; init; }

    /// <summary>Gets the SignalR hub endpoint path.</summary>
    string HubPath { get; init; }
}
