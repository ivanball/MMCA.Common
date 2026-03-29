namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Sends push notifications to connected clients. Infrastructure implementations may use
/// SignalR, Firebase Cloud Messaging, etc.
/// </summary>
public interface IPushNotificationSender
{
    /// <summary>Sends a push notification to a specific user.</summary>
    /// <param name="userId">The target user identifier.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata (e.g., deep-link URL, notification type).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendToUserAsync(UserIdentifierType userId, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a push notification to multiple specific users.</summary>
    /// <param name="userIds">The target user identifiers.</param>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata (e.g., deep-link URL, notification type).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a push notification to all connected clients.</summary>
    /// <param name="title">The notification title.</param>
    /// <param name="body">The notification body text.</param>
    /// <param name="metadata">Optional key-value metadata (e.g., deep-link URL, notification type).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
}
