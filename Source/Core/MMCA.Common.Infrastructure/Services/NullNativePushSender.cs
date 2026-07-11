using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// No-op native push sender (ADR-044). Registered as the default implementation so the third
/// notification channel resolves everywhere and silently does nothing until a downstream host
/// calls <c>AddNativePushNotifications()</c> with an enabled hub configuration.
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
