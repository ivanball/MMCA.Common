namespace MMCA.Common.UI.Services.Capabilities.DeviceStatus;

/// <summary>Carries how long the app spent in the background before it came back.</summary>
/// <param name="backgroundDuration">
/// Elapsed time since the app left the foreground, or <see cref="TimeSpan.Zero"/> when no
/// background entry was recorded (a resume raised without a matching pause).
/// </param>
public sealed class AppResumedEventArgs(TimeSpan backgroundDuration) : EventArgs
{
    /// <summary>How long the app was in the background.</summary>
    public TimeSpan BackgroundDuration { get; } = backgroundDuration;
}
