using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace MMCA.Common.API.Authentication;

/// <summary>
/// One generic OIDC sign-in provider read from <c>OAuth:Oidc:&lt;Name&gt;</c>. The section name is the
/// authentication scheme name, the <c>auth/oauth/{name}</c> route segment and the login provider the
/// app's <c>ExternalLoginAsync</c> receives.
/// </summary>
/// <param name="Name">The section name.</param>
/// <param name="Authority">The issuer the discovery document is read from.</param>
/// <param name="ClientId">The client id registered with the provider.</param>
/// <param name="ClientSecret">The client secret registered with the provider.</param>
/// <param name="Scopes">The scopes requested; always includes <c>openid</c>.</param>
/// <param name="DisplayName">The name shown on the sign-in button.</param>
/// <param name="Domains">Email domains that home-realm discovery maps to this provider.</param>
/// <param name="TrustEmail">Whether the provider's <c>email_verified</c> assertion is passed through.</param>
internal sealed partial record OidcProviderSettings(
    string Name,
    string Authority,
    string ClientId,
    string ClientSecret,
    IReadOnlyList<string> Scopes,
    string DisplayName,
    IReadOnlyList<string> Domains,
    bool TrustEmail)
{
    /// <summary>The configuration section holding one child section per provider.</summary>
    internal const string SectionPath = "OAuth:Oidc";

    private static readonly string[] DefaultScopes = ["openid", "profile", "email"];

    /// <summary>
    /// Names an OIDC section may not take: the three built-in providers, whose literal routes and
    /// schemes already exist, and the controller's own literal routes, which would shadow the challenge.
    /// </summary>
    private static readonly FrozenSet<string> ReservedNames = FrozenSet.ToFrozenSet(
        ["google", "github", "apple", "complete", "exchange", "discover"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads every <c>OAuth:Oidc:&lt;Name&gt;</c> section whose <c>ClientId</c> is set (a section without
    /// one stays inert, the same gate the built-in providers use) and fails on a misconfigured one.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The configured providers, in configuration order.</returns>
    /// <exception cref="InvalidOperationException">
    /// A section uses a reserved or malformed name, lacks <c>Authority</c> or <c>ClientSecret</c>, or
    /// claims an email domain another provider already claims. The message names the key.
    /// </exception>
    internal static IReadOnlyList<OidcProviderSettings> ReadAll(IConfiguration configuration)
    {
        // Null-tolerant: an IConfiguration test double whose GetSection returns null means "none".
        var sections = configuration.GetSection(SectionPath)?.GetChildren() ?? [];
        var providers = new List<OidcProviderSettings>();
        var domainOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections.Where(section => !string.IsNullOrEmpty(section["ClientId"])))
        {
            var provider = Read(section);
            var claimed = provider.Domains.FirstOrDefault(domain => !domainOwners.TryAdd(domain, provider.Name));
            if (claimed is not null)
            {
                throw new InvalidOperationException(
                    $"{SectionPath}:{provider.Name}:Domains lists '{claimed}', which {SectionPath}:{domainOwners[claimed]} already claims. " +
                    "Each email domain may map to one provider only.");
            }

            providers.Add(provider);
        }

        return providers;
    }

    private static OidcProviderSettings Read(IConfigurationSection section)
    {
        var name = section.Key;
        var prefix = $"{SectionPath}:{name}";

        if (ReservedNames.Contains(name))
        {
            throw new InvalidOperationException(
                $"{prefix}: the name '{name}' is reserved for a built-in provider or route. Choose another section name.");
        }

        if (!ValidName.IsMatch(name))
        {
            throw new InvalidOperationException(
                $"{prefix}: the name must be 1-64 letters, digits, '-' or '_', starting with a letter or digit, because it becomes a URL path segment.");
        }

        var authority = Required(section, prefix, "Authority");
        var clientSecret = Required(section, prefix, "ClientSecret");

        var scopes = section.GetSection("Scopes").GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(scope => !string.IsNullOrEmpty(scope))
            .Cast<string>()
            .ToList();
        if (scopes.Count == 0)
        {
            scopes.AddRange(DefaultScopes);
        }
        else if (!scopes.Contains("openid", StringComparer.Ordinal))
        {
            scopes.Insert(0, "openid");
        }

        var domains = section.GetSection("Domains").GetChildren()
            .Select(child => NormalizeDomain(child.Value))
            .Where(domain => domain.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var displayName = section["DisplayName"];
        var trustEmail = bool.TryParse(section["TrustEmail"], out var trust) && trust;

        return new OidcProviderSettings(
            name,
            authority,
            section["ClientId"]!,
            clientSecret,
            scopes,
            string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
            domains,
            trustEmail);
    }

    private static string Required(IConfigurationSection section, string prefix, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{prefix}:{key} is required when {prefix}:ClientId is set.")
            : value;
    }

    /// <summary>
    /// Drops a leading <c>@</c> and surrounding whitespace from a domain. Domains are compared
    /// ordinal-ignore-case everywhere, so the case is kept as configured.
    /// </summary>
    /// <param name="domain">The configured or typed domain.</param>
    /// <returns>The comparable form, or an empty string.</returns>
    internal static string NormalizeDomain(string? domain) =>
        (domain ?? string.Empty).Trim().TrimStart('@');

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValidName { get; }
}
