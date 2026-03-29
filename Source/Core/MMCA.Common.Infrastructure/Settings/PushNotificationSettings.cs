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
}
