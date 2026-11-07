namespace MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

/// <summary>The unit a <see cref="NativePushOutcome"/> counts in, which differs by provider.</summary>
public enum NativePushDeliveryUnit
{
    /// <summary>Nothing was attempted.</summary>
    None = 0,

    /// <summary>One send per batch of users (Azure Notification Hubs: one tag-expression chunk per platform).</summary>
    Batch = 1,

    /// <summary>One send per device endpoint (SNS mobile push).</summary>
    Endpoint = 2,

    /// <summary>One send per registration token (Firebase Cloud Messaging).</summary>
    Message = 3,
}
