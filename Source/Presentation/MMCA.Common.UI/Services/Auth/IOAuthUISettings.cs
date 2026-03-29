namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Provides UI-layer configuration for external OAuth providers.
/// Implementations declare which providers are available so the login page
/// can conditionally render social login buttons. The default (no-op) implementation
/// returns no providers, meaning social login buttons are hidden.
/// </summary>
public interface IOAuthUISettings
{
    /// <summary>Whether Google login is available.</summary>
    bool GoogleEnabled => false;

    /// <summary>Whether GitHub login is available.</summary>
    bool GitHubEnabled => false;
}
