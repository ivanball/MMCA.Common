namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Response returned after successful authentication, containing JWT tokens and expiry metadata.
/// Shared between the Identity API and UI clients.
/// </summary>
/// <param name="AccessToken">The JWT access token for API authorization.</param>
/// <param name="RefreshToken">The opaque refresh token used to obtain new access tokens.</param>
/// <param name="AccessTokenExpiry">The UTC expiration time of the access token.</param>
public readonly record struct AuthenticationResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiry);
