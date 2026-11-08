using Microsoft.Extensions.Configuration;

namespace MMCA.Common.UI.Services.Auth.OAuth;

/// <summary>
/// <see cref="IOAuthUISettings"/> implementation that reads provider availability from the
/// <c>OAuth</c> configuration section. Register as a singleton to replace the default no-op
/// implementation. Covers both host shapes with one class: a server host exposes a provider when its
/// <c>OAuth:{Provider}:ClientId</c> is configured; a WASM client receives pre-computed
/// <c>OAuth:{Provider}Enabled</c> flags via its runtime configuration (<c>/client-config</c>), which
/// never carries the client id itself.
/// <para>
/// Generic OIDC providers follow the same two shapes under <c>OAuth:Oidc:&lt;Name&gt;</c>: a server
/// host lists a section whose <c>ClientId</c> is set (taking <c>DisplayName</c> and whether
/// <c>Domains</c> has entries), a WASM client lists a section flagged <c>Enabled</c> (taking
/// <c>DisplayName</c> and a <c>UsesDomains</c> flag). The built-in provider names are never listed.
/// </para>
/// </summary>
public sealed class ConfigurationOAuthUISettings : IOAuthUISettings
{
    private static readonly HashSet<string> ReservedOidcNames = new(
        ["google", "github", "apple", "complete", "exchange", "discover"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether Google login is available.</summary>
    public bool GoogleEnabled { get; }

    /// <summary>Whether GitHub login is available.</summary>
    public bool GitHubEnabled { get; }

    /// <summary>Whether Sign in with Apple is available.</summary>
    public bool AppleEnabled { get; }

    /// <summary>The configured OIDC providers, in configuration order.</summary>
    public IReadOnlyList<OidcProviderOption> OidcProviders { get; }

    public ConfigurationOAuthUISettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var oauth = configuration.GetSection("OAuth");
        GoogleEnabled = IsProviderEnabled(oauth, "Google");
        GitHubEnabled = IsProviderEnabled(oauth, "GitHub");
        AppleEnabled = IsProviderEnabled(oauth, "Apple");
        OidcProviders = ReadOidcProviders(oauth.GetSection("Oidc"));
    }

    private static bool IsProviderEnabled(IConfigurationSection oauth, string provider)
    {
        var flagEnabled = bool.TryParse(oauth[$"{provider}Enabled"], out var flag) && flag;
        return flagEnabled || !string.IsNullOrEmpty(oauth[$"{provider}:ClientId"]);
    }

    private static List<OidcProviderOption> ReadOidcProviders(IConfigurationSection oidc) =>
        [.. oidc.GetChildren()
            .Where(section => !ReservedOidcNames.Contains(section.Key) && IsOidcProviderEnabled(section))
            .Select(section => new OidcProviderOption(
                section.Key,
                string.IsNullOrWhiteSpace(section["DisplayName"]) ? section.Key : section["DisplayName"]!.Trim(),
                UsesDomains(section)))];

    private static bool IsOidcProviderEnabled(IConfigurationSection section) =>
        bool.TryParse(section["Enabled"], out var flag) && flag || !string.IsNullOrEmpty(section["ClientId"]);

    private static bool UsesDomains(IConfigurationSection section) =>
        bool.TryParse(section["UsesDomains"], out var flag) && flag
        || section.GetSection("Domains").GetChildren().Any(domain => !string.IsNullOrWhiteSpace(domain.Value));
}
