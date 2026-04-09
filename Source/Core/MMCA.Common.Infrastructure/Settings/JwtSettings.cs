using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete JWT settings bound from the <c>Jwt</c> configuration section.
/// Issuer and audience are always required. The signing key requirement depends on
/// <see cref="SigningAlgorithm"/>:
/// <list type="bullet">
///   <item><see cref="JwtSigningAlgorithm.HS256"/> (default): <see cref="SecretForKey"/> is required (Base64 HMAC key, min 32 chars).</item>
///   <item><see cref="JwtSigningAlgorithm.RS256"/>: <see cref="RsaPrivateKeyPem"/> is required for issuers; <see cref="RsaPublicKeyPem"/> is required for in-process validators.</item>
/// </list>
/// Implements <see cref="IValidatableObject"/> so key-material validation is
/// conditional on the selected <see cref="SigningAlgorithm"/>.
/// </summary>
public sealed class JwtSettings : IJwtSettings, IValidatableObject
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Jwt";

    /// <inheritdoc />
    public JwtSigningAlgorithm SigningAlgorithm { get; init; } = JwtSigningAlgorithm.HS256;

    /// <inheritdoc />
    public string SecretForKey { get; init; } = string.Empty;

    /// <inheritdoc />
    public string? RsaPrivateKeyPem { get; init; }

    /// <inheritdoc />
    public string? RsaPublicKeyPem { get; init; }

    /// <inheritdoc />
    [Required]
    public string Issuer { get; init; } = string.Empty;

    /// <inheritdoc />
    [Required]
    public string Audience { get; init; } = string.Empty;

    /// <inheritdoc />
    public int AccessTokenExpirationMinutes { get; init; } = 15;

    /// <inheritdoc />
    public int RefreshTokenExpirationDays { get; init; } = 7;

    /// <summary>
    /// Algorithm-aware validation: HS256 requires <see cref="SecretForKey"/> (min 32 chars),
    /// RS256 requires <see cref="RsaPrivateKeyPem"/>.
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
