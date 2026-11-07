using System.Security.Claims;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Auth;

/// <summary>
/// Generates and validates JWT access and refresh tokens for authentication.
/// </summary>
public interface ITokenService
{
    /// <summary>Generates a signed JWT access token containing user claims.</summary>
    /// <param name="userId">The user's identifier.</param>
    /// <param name="email">The user's email address.</param>
    /// <param name="role">The user's role, as the host names it.</param>
    /// <param name="fullName">The user's full display name.</param>
    /// <param name="additionalClaims">Optional module-specific claims to include in the token.</param>
    /// <returns>The signed JWT token string.</returns>
    string GenerateAccessToken(
        UserIdentifierType userId,
        string email,
        string role,
        string fullName,
        IEnumerable<Claim>? additionalClaims = null);

    /// <summary>
    /// Generates a signed JWT access token that expires after <paramref name="lifetime"/> instead of
    /// the configured access-token lifetime. Used for the read-only view-as token (ADR-137), which
    /// must never outlive its 15-minute grant.
    /// </summary>
    /// <remarks>
    /// The default ignores <paramref name="lifetime"/> and mints with the configured lifetime, so a
    /// hand-written token service keeps compiling; the framework's <c>TokenService</c> honors it. The
    /// view-as guard checks the grant's own expiry on every request either way, so a longer-lived
    /// token is still refused once its grant expires.
    /// </remarks>
    /// <param name="userId">The user's identifier.</param>
    /// <param name="email">The user's email address.</param>
    /// <param name="role">The user's role, as the host names it.</param>
    /// <param name="fullName">The user's full display name.</param>
    /// <param name="additionalClaims">Additional claims to include in the token.</param>
    /// <param name="lifetime">How long the token is valid for.</param>
    /// <returns>The signed JWT token string.</returns>
    string GenerateShortLivedAccessToken(
        UserIdentifierType userId,
        string email,
        string role,
        string fullName,
        IEnumerable<Claim>? additionalClaims,
        TimeSpan lifetime) =>
        GenerateAccessToken(userId, email, role, fullName, additionalClaims);

    /// <summary>Generates a cryptographically random refresh token.</summary>
    /// <returns>A base64-encoded refresh token string.</returns>
    string GenerateRefreshToken();

    /// <summary>
    /// Lifetime of issued access tokens. The concrete implementation derives it from the bound
    /// JWT settings so the expiry reported to clients matches the token's actual <c>exp</c>;
    /// the default keeps hand-written test doubles on the BR-205 baseline (15 minutes).
    /// </summary>
    TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(15);

    /// <summary>
    /// Absolute lifetime of issued refresh tokens (BR-205). The concrete implementation derives
    /// it from the bound JWT settings; the default keeps hand-written test doubles on the
    /// BR-205 baseline (7 days).
    /// </summary>
    TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(7);

    /// <summary>
    /// Extracts claims from an expired access token without validating its lifetime.
    /// Used during token refresh to identify the user.
    /// </summary>
    /// <param name="token">The expired JWT token string.</param>
    /// <returns>The claims principal, or <see langword="null"/> if the token is invalid.</returns>
    ClaimsPrincipal? GetPrincipalFromExpiredToken(string token);
}
