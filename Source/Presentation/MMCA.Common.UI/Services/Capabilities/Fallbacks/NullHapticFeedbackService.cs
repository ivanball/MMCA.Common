namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IHapticFeedbackService"/>: no haptics hardware; every call is a no-op.</summary>
public sealed class NullHapticFeedbackService : IHapticFeedbackService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public void Click()
    {
        // No haptics on this host.
    }

    /// <inheritdoc />
    public void LongPress()
    {
        // No haptics on this host.
    }

    /// <inheritdoc />
    public void Vibrate(TimeSpan duration)
    {
        // No haptics on this host.
    }
}
