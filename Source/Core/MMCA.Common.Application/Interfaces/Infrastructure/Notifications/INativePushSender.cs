namespace MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

/// <summary>
/// Sends OS-level push notifications to registered device installations (ADR-044) - the third
/// delivery channel beside the persisted inbox record and the SignalR real-time push handled by
/// <see cref="IPushNotificationSender"/>. Reaches devices when the app is backgrounded or killed.
/// Adapter packages supply the implementation (MMCA.Common.Azure targets Azure Notification Hubs,
/// MMCA.Common.Aws targets SNS mobile push, both FCM v1 + APNs; MMCA.Common.Gcp targets Firebase
/// Cloud Messaging with FCM registration tokens on both platforms); the default is a no-op until an
/// adapter is configured.
/// </summary>
public interface INativePushSender
{
    /// <summary>
    /// Sends a native push to every registered installation of the given users and reports, per
    /// platform, how many sends succeeded and failed. A failing send is counted and the rest still
    /// go out: an implementation returns the failure in <see cref="NativePushOutcome.FirstFailure"/>
    /// instead of throwing. Only the caller's own cancellation propagates.
    /// </summary>
    /// <param name="userIds">The target user identifiers (resolved to installations via user tags).</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata (e.g., deep-link route) carried in the platform payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-platform outcome; <see cref="NativePushOutcome.None"/> when nothing was attempted.</returns>
    Task<NativePushOutcome> SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
}
