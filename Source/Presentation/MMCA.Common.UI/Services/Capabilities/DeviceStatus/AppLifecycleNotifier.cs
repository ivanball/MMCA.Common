namespace MMCA.Common.UI.Services.Capabilities.DeviceStatus;

/// <summary>
/// Default <see cref="IAppLifecycleNotifier"/>: a singleton the native host signals from outside any
/// DI scope, timestamping the background entry so the resume can report how long the app was away.
/// Inert on web heads, which never signal it.
/// </summary>
/// <param name="timeProvider">Clock used to measure the background interval; defaults to the system clock.</param>
public sealed class AppLifecycleNotifier(TimeProvider? timeProvider = null) : IAppLifecycleNotifier
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private DateTimeOffset? _backgroundedAt;

    /// <inheritdoc />
    public event EventHandler<AppResumedEventArgs>? Resumed;

    /// <inheritdoc />
    public void NotifyEnteredBackground()
    {
        lock (_gate)
        {
            _backgroundedAt = _timeProvider.GetUtcNow();
        }
    }

    /// <inheritdoc />
    public void NotifyResumed()
    {
        TimeSpan away;

        lock (_gate)
        {
            away = _backgroundedAt is { } since
                ? _timeProvider.GetUtcNow() - since
                : TimeSpan.Zero;
            _backgroundedAt = null;
        }

        Resumed?.Invoke(this, new AppResumedEventArgs(away < TimeSpan.Zero ? TimeSpan.Zero : away));
    }
}
