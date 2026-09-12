using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Shared.Notifications;

/// <summary>
/// Feature flag constants for the Notification module.
/// </summary>
public static class NotificationFeatures
{
    /// <summary>Feature flag controlling push notification functionality.</summary>
    [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "MMCA.Common")]
    public const string PushNotifications = "Notification.PushNotifications";
}
