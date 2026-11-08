namespace MMCA.Common.Shared.Auth.ExternalProviders;

/// <summary>
/// Home-realm discovery answer from <c>GET auth/oauth/discover?email=</c>: the configured OIDC
/// provider whose <c>Domains</c> list holds the address's domain. The sign-in page then starts the
/// challenge at <c>auth/oauth/{Provider}</c>.
/// </summary>
/// <param name="Provider">The provider name, which is also its scheme name and route segment.</param>
/// <param name="DisplayName">The name to show the user, for example "Contoso".</param>
public sealed record OidcDiscoveryResponse(string Provider, string DisplayName);
