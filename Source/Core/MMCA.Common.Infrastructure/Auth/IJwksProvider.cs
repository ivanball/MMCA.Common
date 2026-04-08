using Microsoft.IdentityModel.Tokens;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Provides the active <see cref="JsonWebKeySet"/> served at <c>/.well-known/jwks.json</c>.
/// Implementations are responsible for materializing the public key(s) the Identity service
/// uses to sign access tokens, in the JWK format consumed by other services that validate
/// those tokens via <c>AddForwardedJwtBearer</c>.
/// </summary>
public interface IJwksProvider
{
    /// <summary>
    /// Returns the JWKS document for this service. Implementations should return an empty key set
    /// (rather than throwing) when no signing key is configured, so that <c>/.well-known/jwks.json</c>
    /// remains a valid endpoint that downstream services can poll without errors.
    /// </summary>
    /// <returns>The active <see cref="JsonWebKeySet"/>.</returns>
    JsonWebKeySet GetJsonWebKeySet();
}
