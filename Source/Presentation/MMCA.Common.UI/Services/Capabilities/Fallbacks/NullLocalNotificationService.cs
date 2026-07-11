namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="ILocalNotificationService"/>: scheduling unavailable; hosts hide reminder settings.</summary>
public sealed class NullLocalNotificationService : ILocalNotificationService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<bool> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public Task ScheduleAsync(LocalNotificationRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task CancelAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
