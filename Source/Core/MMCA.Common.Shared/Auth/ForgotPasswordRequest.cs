namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for starting a password reset. The response is always accepted, so the payload
/// carries no signal about whether the address belongs to an account.
/// </summary>
/// <param name="Email">The email address to send the reset instructions to.</param>
public readonly record struct ForgotPasswordRequest(
    string Email);
