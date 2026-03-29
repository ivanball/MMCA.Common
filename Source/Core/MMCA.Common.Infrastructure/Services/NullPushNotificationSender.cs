using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// No-op push notification sender. Registered as the default implementation so that
/// DI resolves successfully when push notifications are not configured. Downstream apps
/// override this with <see cref="SignalRPushNotificationSender"/> via <c>AddPushNotifications()</c>.
/// </summary>
public sealed class NullPushNotificationSender : IPushNotificationSender
{
    /// <inheritdoc />
    public Task SendToUserAsync(UserIdentifierType userId, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task SendToUsersAsync(IEnumerable<UserIdentifierType> userIds, string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task BroadcastAsync(string title, string body, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
