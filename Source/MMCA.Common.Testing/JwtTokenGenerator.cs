using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace MMCA.Common.Testing;

/// <summary>
/// Generates JWT tokens for integration tests. Each downstream project extends this with
/// role-specific convenience methods (e.g., AdminToken, OrganizerToken).
/// The secret key, issuer, and audience must match the test appsettings override.
/// </summary>
public static class JwtTokenGenerator
{
    /// <summary>Default test secret key (base64-encoded, 256-bit HMAC-SHA256).</summary>
    public const string DefaultSecretKey = "RgDldLrK+p+T0JisAKdD7THnT/npmWYl4vV3UUiRSVE=";

    /// <summary>Default test issuer.</summary>
    public const string DefaultIssuer = "https://localhost:6001";

    /// <summary>
    /// Generates a signed JWT token with the given user ID, role, and optional additional claims.
    /// </summary>
    /// <param name="audience">The token audience (must match the app's JwtSettings).</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="role">The user's role claim.</param>
    /// <param name="additionalClaims">Optional extra claims (e.g., customer_id, speaker_id).</param>
    /// <param name="secretKey">Override the default signing key.</param>
    /// <param name="issuer">Override the default issuer.</param>
    /// <returns>A signed JWT string.</returns>
    public static string GenerateToken(
        string audience,
        UserIdentifierType userId,
        string role,
        IEnumerable<Claim>? additionalClaims = null,
        string secretKey = DefaultSecretKey,
        string issuer = DefaultIssuer)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new("user_id", userId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Role, role),
        };

        if (additionalClaims is not null)
        {
            claims.AddRange(additionalClaims);
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
