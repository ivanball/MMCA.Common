namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for obtaining a new access token using a refresh token.
/// The expired access token is included so the server can extract claims without re-authentication.
/// </summary>
/// <param name="AccessToken">The expired (or about-to-expire) JWT access token.</param>
/// <param name="RefreshToken">The opaque refresh token issued during login.</param>
public readonly record struct RefreshTokenRequest(
    string AccessToken,
    string RefreshToken);
