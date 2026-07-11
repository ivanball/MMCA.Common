namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Device-preference keys shared by the framework's device-settings surfaces and gates.
/// Stored via <see cref="IDevicePreferences"/>, so they describe THIS device and never roam.
/// </summary>
public static class DevicePreferenceKeys
{
    /// <summary>Whether the biometric app-lock guards stored-token auto-login (ADR-042 Wave 4).</summary>
    public const string AppLockEnabled = "applock.enabled";
}
