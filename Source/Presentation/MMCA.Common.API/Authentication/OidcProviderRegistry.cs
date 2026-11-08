namespace MMCA.Common.API.Authentication;

/// <summary>
/// The OIDC providers <see cref="ExternalAuthExtensions.AddExternalAuthProviders"/> registered, read
/// once at startup. <c>OAuthControllerBase</c> resolves it from the request services to decide which
/// names the generic challenge route accepts and to answer home-realm discovery. Registered only when
/// at least one provider is configured, so its absence means "no OIDC providers".
/// </summary>
/// <param name="providers">The configured providers.</param>
internal sealed class OidcProviderRegistry(IReadOnlyList<OidcProviderSettings> providers)
{
    /// <summary>Finds a configured provider by name, ignoring case.</summary>
    /// <param name="name">The route segment.</param>
    /// <returns>The provider, or <see langword="null"/>.</returns>
    public OidcProviderSettings? Find(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the provider whose <c>Domains</c> holds the address's domain (exact match, ignoring case;
    /// a subdomain does not match its parent).
    /// </summary>
    /// <param name="email">The address the user typed.</param>
    /// <returns>The provider, or <see langword="null"/> for a malformed address or an unclaimed domain.</returns>
    public OidcProviderSettings? FindByEmail(string? email)
    {
        var at = email?.LastIndexOf('@') ?? -1;
        if (at <= 0 || at == email!.Length - 1)
        {
            return null;
        }

        var domain = OidcProviderSettings.NormalizeDomain(email[(at + 1)..]);
        return domain.Length == 0
            ? null
            : providers.FirstOrDefault(p => p.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase));
    }
}
