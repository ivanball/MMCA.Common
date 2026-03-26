namespace MMCA.Common.UI.Services;

/// <summary>
/// Abstracts device/hosting-environment detection so shared UI components can adapt behavior.
/// Each host (Blazor Server, WebAssembly, MAUI) provides its own implementation.
/// </summary>
public interface IFormFactor
{
    /// <summary>Returns the device form factor (e.g., "Web", "WebAssembly", "Phone").</summary>
    string GetFormFactor();

    /// <summary>Returns the platform/OS description.</summary>
    string GetPlatform();
}
