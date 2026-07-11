namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Sends OS-level push notifications to registered device installations (ADR-044) - the third
/// delivery channel beside the persisted inbox record and the SignalR real-time push handled by
/// <see cref="IPushNotificationSender"/>. Reaches devices when the app is backgrounded or killed.
/// Infrastructure implementations target Azure Notification Hubs (FCM v1 + APNs); the default is
/// a no-op until a hub is configured.
/// </summary>
public interface INativePushSender
{
    /// <summary>Sends a native push to every registered installation of the given users.</summary>
    /// <param name="userIds">The target user identifiers (resolved to installations via user tags).</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata (e.g., deep-link route) carried in the platform payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a native push to every registered installation.</summary>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata (e.g., deep-link route) carried in the platform payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
}
