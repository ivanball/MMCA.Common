using System.Security.Claims;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Generates and validates JWT access and refresh tokens for authentication.
/// </summary>
public interface ITokenService
{
    /// <summary>Generates a signed JWT access token containing user claims.</summary>
    /// <param name="userId">The user's identifier.</param>
    /// <param name="email">The user's email address.</param>
    /// <param name="role">The user's role (e.g. "Organizer", "Attendee").</param>
    /// <param name="fullName">The user's full display name.</param>
    /// <param name="additionalClaims">Optional module-specific claims to include in the token.</param>
    /// <returns>The signed JWT token string.</returns>
    string GenerateAccessToken(
        UserIdentifierType userId,
        string email,
        string role,
        string fullName,
        IEnumerable<Claim>? additionalClaims = null);

    /// <summary>Generates a cryptographically random refresh token.</summary>
    /// <returns>A base64-encoded refresh token string.</returns>
    string GenerateRefreshToken();

    /// <summary>
    /// Extracts claims from an expired access token without validating its lifetime.
    /// Used during token refresh to identify the user.
    /// </summary>
    /// <param name="token">The expired JWT token string.</param>
    /// <returns>The claims principal, or <see langword="null"/> if the token is invalid.</returns>
    ClaimsPrincipal? GetPrincipalFromExpiredToken(string token);
}
