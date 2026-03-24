namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for email/password authentication.
/// </summary>
/// <param name="Email">The user's email address.</param>
/// <param name="Password">The user's password (transmitted over TLS, never logged).</param>
public readonly record struct LoginRequest(
    string Email,
    string Password);
