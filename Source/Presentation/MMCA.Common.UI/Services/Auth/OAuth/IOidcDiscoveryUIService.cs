using MMCA.Common.Shared.Auth.ExternalProviders;

namespace MMCA.Common.UI.Services.Auth.OAuth;

/// <summary>
/// The client behind the sign-in page's "Sign in with your organization" step: asks the API which
/// configured OIDC provider owns an email address's domain (home-realm discovery).
/// </summary>
public interface IOidcDiscoveryUIService
{
    /// <summary>Looks up the OIDC provider configured for the address's domain.</summary>
    /// <param name="email">The address the user typed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider, or <see langword="null"/> when no provider claims the domain.</returns>
    Task<OidcDiscoveryResponse?> DiscoverAsync(string email, CancellationToken cancellationToken = default);
}
