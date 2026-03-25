using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Concrete JWT settings bound from the <c>Jwt</c> configuration section.
/// Key, issuer, and audience are required; token lifetimes have sensible defaults.
/// </summary>
public sealed class JwtSettings : IJwtSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Jwt";

    /// <inheritdoc />
    [Required]
    public string SecretForKey { get; init; } = string.Empty;

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
