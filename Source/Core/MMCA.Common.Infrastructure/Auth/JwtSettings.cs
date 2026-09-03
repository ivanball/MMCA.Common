using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Concrete JWT settings bound from the <c>Jwt</c> configuration section.
/// Issuer and audience are always required. The signing key requirement depends on
/// <see cref="SigningAlgorithm"/>:
/// <list type="bullet">
///   <item><see cref="JwtSigningAlgorithm.RS256"/> (default): <see cref="RsaPrivateKeyPem"/> is required for issuers; <see cref="RsaPublicKeyPem"/> is required for in-process validators.</item>
///   <item><see cref="JwtSigningAlgorithm.HS256"/>: <see cref="SecretForKey"/> is required (Base64 HMAC key, min 32 chars).</item>
/// </list>
/// Implements <see cref="IValidatableObject"/> so key-material validation is
/// conditional on the selected <see cref="SigningAlgorithm"/>.
/// </summary>
public sealed class JwtSettings : IValidatableObject
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Jwt";

    /// <summary>
    /// Gets the algorithm used to sign access tokens and the key type used to validate them.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="JwtSigningAlgorithm.RS256"/>: asymmetric signing is what lets a
    /// validator verify a token without holding the key that mints one, so a host that never sets
    /// <c>Jwt:SigningAlgorithm</c> gets the algorithm that survives extraction. A single-host
    /// monolith opts into <see cref="JwtSigningAlgorithm.HS256"/> explicitly.
    /// </remarks>
    public JwtSigningAlgorithm SigningAlgorithm { get; init; } = JwtSigningAlgorithm.RS256;

    /// <summary>
    /// Gets the Base64-encoded symmetric HMAC key used when <see cref="SigningAlgorithm"/> is
    /// <see cref="JwtSigningAlgorithm.HS256"/>. Supply it through user-secrets or environment
    /// variables, never through a checked-in <c>appsettings.json</c>.
    /// </summary>
    public string SecretForKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the PEM-encoded RSA private key the issuer signs with when
    /// <see cref="SigningAlgorithm"/> is <see cref="JwtSigningAlgorithm.RS256"/>.
    /// </summary>
    public string? RsaPrivateKeyPem { get; init; }

    /// <summary>
    /// Gets the PEM-encoded RSA public key an in-process validator verifies with when
    /// <see cref="SigningAlgorithm"/> is <see cref="JwtSigningAlgorithm.RS256"/>. A service that
    /// fetches the key through JWKS at runtime leaves this unset.
    /// </summary>
    public string? RsaPublicKeyPem { get; init; }

    /// <summary>Gets the token issuer (<c>iss</c>), required for every algorithm.</summary>
    [Required]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Gets the token audience (<c>aud</c>), required for every algorithm.</summary>
    [Required]
    public string Audience { get; init; } = string.Empty;

    /// <summary>Gets the access-token lifetime in minutes. Defaults to <c>15</c>.</summary>
    public int AccessTokenExpirationMinutes { get; init; } = 15;

    /// <summary>Gets the refresh-token lifetime in days. Defaults to <c>7</c>.</summary>
    public int RefreshTokenExpirationDays { get; init; } = 7;

    /// <summary>
    /// Algorithm-aware validation: RS256 (the default) requires <see cref="RsaPrivateKeyPem"/>,
    /// HS256 requires <see cref="SecretForKey"/> (min 32 chars).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SigningAlgorithm == JwtSigningAlgorithm.HS256 && SecretForKey.Length < 32)
        {
            yield return new ValidationResult(
                "SecretForKey must be at least 32 characters when SigningAlgorithm is HS256. Replace the placeholder value with a real secret via user-secrets or environment variables.",
                [nameof(SecretForKey)]);
        }

        if (SigningAlgorithm == JwtSigningAlgorithm.RS256 && string.IsNullOrWhiteSpace(RsaPrivateKeyPem))
        {
            yield return new ValidationResult(
                "RsaPrivateKeyPem is required when SigningAlgorithm is RS256.",
                [nameof(RsaPrivateKeyPem)]);
        }
    }
}
