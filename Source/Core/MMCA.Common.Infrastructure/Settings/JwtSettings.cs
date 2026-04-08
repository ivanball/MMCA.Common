using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete JWT settings bound from the <c>Jwt</c> configuration section.
/// Issuer and audience are always required. The signing key requirement depends on
/// <see cref="SigningAlgorithm"/>:
/// <list type="bullet">
///   <item><see cref="JwtSigningAlgorithm.HS256"/> (default): <see cref="SecretForKey"/> is required (Base64 HMAC key).</item>
///   <item><see cref="JwtSigningAlgorithm.RS256"/>: <see cref="RsaPrivateKeyPem"/> is required for issuers; <see cref="RsaPublicKeyPem"/> is required for issuers and validators sharing the in-process key.</item>
/// </list>
/// The DataAnnotations validators only enforce the HS256 baseline. RS256 deployments
/// must validate their own RSA key material at startup (e.g., by trying to load the PEM
/// in <c>TokenService</c>'s constructor or via a custom <c>IValidateOptions</c>).
/// </summary>
public sealed class JwtSettings : IJwtSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Jwt";

    /// <inheritdoc />
    public JwtSigningAlgorithm SigningAlgorithm { get; init; } = JwtSigningAlgorithm.HS256;

    /// <inheritdoc />
    [MinLength(32, ErrorMessage = "SecretForKey must be at least 32 characters when SigningAlgorithm is HS256. Replace the placeholder value with a real secret via user-secrets or environment variables.")]
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
}
