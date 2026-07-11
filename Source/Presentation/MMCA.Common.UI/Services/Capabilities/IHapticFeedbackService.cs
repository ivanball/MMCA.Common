namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Fires tactile feedback on interactions (bookmark toggles, poll votes). Native-only:
/// the web fallback is a hidden no-op with <see cref="IsSupported"/> <see langword="false"/>.
/// Failures are swallowed — haptics are decoration, never behavior.
/// </summary>
public interface IHapticFeedbackService
{
    /// <summary>Whether the platform can produce haptic feedback.</summary>
    bool IsSupported { get; }

    /// <summary>Short click feedback for taps and toggles.</summary>
    void Click();

    /// <summary>Stronger feedback for long-press style interactions.</summary>
    void LongPress();

    /// <summary>Raw vibration for attention-level cues (e.g. notification arrival while foregrounded).</summary>
    void Vibrate(TimeSpan duration);
}
