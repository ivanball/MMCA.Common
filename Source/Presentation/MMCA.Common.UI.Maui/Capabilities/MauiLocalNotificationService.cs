using MMCA.Common.UI.Services.Capabilities;
using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="ILocalNotificationService"/> over Plugin.LocalNotification. Permission
/// maps to Android 13+ <c>POST_NOTIFICATIONS</c> / iOS notification authorization; scheduling
/// uses inexact platform alarms (no <c>SCHEDULE_EXACT_ALARM</c> — Play policy). Taps are
/// routed to <see cref="IDeepLinkDispatcher"/> by the package bootstrap, not here.
/// </summary>
public sealed class MauiLocalNotificationService : ILocalNotificationService
{
    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<bool> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await LocalNotificationCenter.Current.AreNotificationsEnabled().ConfigureAwait(false))
            {
                return true;
            }

            return await LocalNotificationCenter.Current.RequestNotificationPermission().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ScheduleAsync(LocalNotificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.DeliverAt <= DateTimeOffset.Now)
        {
            return;
        }

        var platformRequest = new NotificationRequest
        {
            NotificationId = request.Id,
            Title = request.Title,
            Description = request.Body,
            ReturningData = request.DeepLinkRoute ?? string.Empty,
            Schedule = new NotificationRequestSchedule
            {
                NotifyTime = request.DeliverAt,
            },
        };

        try
        {
            await LocalNotificationCenter.Current.Show(platformRequest).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Notifications unavailable (permission revoked mid-session) — reminder becomes a no-op.
        }
    }

    /// <inheritdoc />
    public Task CancelAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count > 0)
        {
            LocalNotificationCenter.Current.Cancel([.. ids]);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        LocalNotificationCenter.Current.CancelAll();
        return Task.CompletedTask;
    }
}
