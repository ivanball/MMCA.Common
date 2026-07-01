using Microsoft.Extensions.Configuration;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// <see cref="IOAuthUISettings"/> implementation that reads provider availability from the
/// <c>OAuth</c> configuration section. Register as a singleton to replace the default no-op
/// implementation. Covers both host shapes with one class: a server host exposes a provider when its
/// <c>OAuth:{Provider}:ClientId</c> is configured; a WASM client receives pre-computed
/// <c>OAuth:{Provider}Enabled</c> flags via its runtime configuration (<c>/client-config</c>), which
/// never carries the client id itself.
/// </summary>
public sealed class ConfigurationOAuthUISettings : IOAuthUISettings
{
    /// <summary>Whether Google login is available.</summary>
    public bool GoogleEnabled { get; }

    /// <summary>Whether GitHub login is available.</summary>
    public bool GitHubEnabled { get; }

    public ConfigurationOAuthUISettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var oauth = configuration.GetSection("OAuth");
        GoogleEnabled = IsProviderEnabled(oauth, "Google");
        GitHubEnabled = IsProviderEnabled(oauth, "GitHub");
    }

    private static bool IsProviderEnabled(IConfigurationSection oauth, string provider)
    {
        var flagEnabled = bool.TryParse(oauth[$"{provider}Enabled"], out var flag) && flag;
        return flagEnabled || !string.IsNullOrEmpty(oauth[$"{provider}:ClientId"]);
    }
}
