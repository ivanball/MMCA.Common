using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

namespace MMCA.Common.Infrastructure.Notifications.Push;

/// <summary>
/// No-op native push sender (ADR-044). Registered as the default implementation so the third
/// notification channel resolves everywhere and silently does nothing until a downstream host
/// calls a push adapter (<c>AddAzureNotificationHubsPush(configuration)</c> from <c>MMCA.Common.Azure</c>,
/// <c>AddAmazonSnsPush(configuration)</c> from <c>MMCA.Common.Aws</c>, or
/// <c>AddFirebaseCloudMessagingPush(configuration)</c> from <c>MMCA.Common.Gcp</c>) with an enabled configuration.
/// </summary>
public sealed class NullNativePushSender : INativePushSender
{
    /// <inheritdoc />
    public Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
