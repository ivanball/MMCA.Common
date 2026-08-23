namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for completing a password reset with a single-use token.
/// </summary>
/// <param name="Email">The email address the reset token was issued for.</param>
/// <param name="Token">The single-use reset token from the reset email.</param>
/// <param name="NewPassword">The desired new password (transmitted over TLS, never logged).</param>
public readonly record struct ResetPasswordRequest(
    string Email,
    string Token,
    string NewPassword);
