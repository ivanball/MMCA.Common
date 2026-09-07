namespace MMCA.Common.UI.Services.Capabilities.DeviceStatus;

/// <summary>
/// Reports the app moving to the background and coming back, so UI that must react to a
/// foreground transition can do so without knowing anything about the native host.
/// <para>
/// SECURITY: this is what lets the app-lock overlay re-arm. A hybrid head keeps its render tree
/// alive across a background/foreground cycle, so a gate that engages only at first render stays
/// open for whoever picks the device up next. Web heads never raise these events, which is correct:
/// there is no app lock there.
/// </para>
/// </summary>
public interface IAppLifecycleNotifier
{
    /// <summary>Raised when the app returns to the foreground, carrying how long it was away.</summary>
    event EventHandler<AppResumedEventArgs>? Resumed;

    /// <summary>Records that the app has left the foreground. Called by the native host.</summary>
    void NotifyEnteredBackground();

    /// <summary>
    /// Records that the app has returned to the foreground and raises <see cref="Resumed"/>.
    /// Called by the native host.
    /// </summary>
    void NotifyResumed();
}
