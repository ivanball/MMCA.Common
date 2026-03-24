namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// JWT authentication configuration. Bound from the <c>Jwt</c> configuration section.
/// </summary>
public interface IJwtSettings
{
    /// <summary>Gets the Base64-encoded HMAC-SHA256 signing key.</summary>
    string SecretForKey { get; init; }

    /// <summary>Gets the token issuer claim value.</summary>
    string Issuer { get; init; }

    /// <summary>Gets the token audience claim value.</summary>
    string Audience { get; init; }

    /// <summary>Gets the access token lifetime in minutes.</summary>
    int AccessTokenExpirationMinutes { get; init; }

    /// <summary>Gets the refresh token lifetime in days.</summary>
    int RefreshTokenExpirationDays { get; init; }
}
