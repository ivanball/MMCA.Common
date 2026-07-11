namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Schedules on-device notifications (session reminders) with no backend involvement.
/// Native-only: web/null fallbacks report <see cref="IsSupported"/> <see langword="false"/>
/// and callers hide the related settings UI. Implementations own the permission flow
/// (Android 13+ <c>POST_NOTIFICATIONS</c>, iOS notification authorization) and never throw
/// on denial — scheduling simply becomes a no-op until permission is granted.
/// </summary>
public interface ILocalNotificationService
{
    /// <summary>Whether this platform can schedule local notifications.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Ensures notification permission, prompting the user if the platform requires consent
    /// and it has not been decided yet. Returns whether notifications are currently permitted.
    /// </summary>
    Task<bool> RequestPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>Schedules (or replaces, by id) a pending notification. No-op without permission.</summary>
    Task ScheduleAsync(LocalNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels the pending notifications with the given ids; unknown ids are ignored.</summary>
    Task CancelAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);

    /// <summary>Cancels every pending notification scheduled by this app.</summary>
    Task CancelAllAsync(CancellationToken cancellationToken = default);
}
