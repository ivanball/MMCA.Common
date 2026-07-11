using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IHapticFeedbackService"/> over <c>HapticFeedback.Default</c> and
/// <c>Vibration.Default</c>. Haptics are decoration: every failure (Windows has no haptics,
/// vibration may be disabled by the OS) is swallowed. Android needs the <c>VIBRATE</c>
/// permission for <see cref="Vibrate"/>.
/// </summary>
public sealed class MauiHapticFeedbackService : IHapticFeedbackService
{
    /// <inheritdoc />
    public bool IsSupported => !OperatingSystem.IsWindows();

    /// <inheritdoc />
    public void Click() => Perform(HapticFeedbackType.Click);

    /// <inheritdoc />
    public void LongPress() => Perform(HapticFeedbackType.LongPress);

    /// <inheritdoc />
    public void Vibrate(TimeSpan duration)
    {
        try
        {
            Vibration.Default.Vibrate(duration);
        }
        catch (FeatureNotSupportedException)
        {
            // No vibration motor / platform support — decoration only.
        }
        catch (PermissionException)
        {
            // VIBRATE permission missing from the host manifest — decoration only.
        }
    }

    private static void Perform(HapticFeedbackType type)
    {
        try
        {
            HapticFeedback.Default.Perform(type);
        }
        catch (FeatureNotSupportedException)
        {
            // No haptics on this platform — decoration only.
        }
        catch (PermissionException)
        {
            // Feedback blocked by the platform — decoration only.
        }
    }
}
