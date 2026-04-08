namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// JWT authentication configuration. Bound from the <c>Jwt</c> configuration section.
/// Supports both symmetric (HMAC) and asymmetric (RSA) signing — see
/// <see cref="SigningAlgorithm"/>. The microservice-extraction migration switches deployments
/// from HMAC to RSA so that the Identity service can sign tokens that other services
/// validate via JWKS without sharing a symmetric secret.
/// </summary>
public interface IJwtSettings
{
    /// <summary>
    /// Gets the algorithm used to sign and validate access tokens. Defaults to
    /// <see cref="JwtSigningAlgorithm.HS256"/> for backwards compatibility.
    /// </summary>
    JwtSigningAlgorithm SigningAlgorithm { get; init; }

    /// <summary>
    /// Gets the Base64-encoded HMAC-SHA256 signing key. Required when
    /// <see cref="SigningAlgorithm"/> is <see cref="JwtSigningAlgorithm.HS256"/>.
    /// </summary>
    string SecretForKey { get; init; }

    /// <summary>
    /// Gets the PEM-encoded RSA private key used to sign tokens when
    /// <see cref="SigningAlgorithm"/> is <see cref="JwtSigningAlgorithm.RS256"/>.
    /// Stored in user-secrets / Key Vault, not in <c>appsettings.json</c>.
    /// </summary>
    string? RsaPrivateKeyPem { get; init; }

    /// <summary>
    /// Gets the PEM-encoded RSA public key used to validate tokens when
    /// <see cref="SigningAlgorithm"/> is <see cref="JwtSigningAlgorithm.RS256"/>. The
    /// Identity service ALSO exposes this key via <c>/.well-known/jwks.json</c> so other
    /// services can fetch it via <c>AddForwardedJwtBearer</c> (in microservice mode).
    /// </summary>
    string? RsaPublicKeyPem { get; init; }

    /// <summary>Gets the token issuer claim value.</summary>
    string Issuer { get; init; }

    /// <summary>Gets the token audience claim value.</summary>
    string Audience { get; init; }

    /// <summary>Gets the access token lifetime in minutes.</summary>
    int AccessTokenExpirationMinutes { get; init; }

    /// <summary>Gets the refresh token lifetime in days.</summary>
    int RefreshTokenExpirationDays { get; init; }
}
