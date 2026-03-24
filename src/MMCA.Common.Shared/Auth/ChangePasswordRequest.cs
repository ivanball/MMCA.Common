namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for changing an authenticated user's password.
/// </summary>
/// <param name="CurrentPassword">The user's current password for verification.</param>
/// <param name="NewPassword">The desired new password.</param>
public readonly record struct ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
