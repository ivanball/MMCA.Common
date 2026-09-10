namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for asking that a fresh confirmation link be emailed to an address.
/// </summary>
/// <remarks>
/// Anonymous by necessity (an unconfirmed account may not be able to sign in at all), and answered
/// identically whether or not the address holds an account, for the same anti-enumeration reason the
/// forgot-password endpoint is.
/// </remarks>
/// <param name="Email">The address to send the confirmation link to.</param>
public readonly record struct SendEmailConfirmationRequest(string Email);

/// <summary>
/// Request payload for redeeming a single-use email-confirmation token.
/// </summary>
/// <param name="Email">The address the token was issued for.</param>
/// <param name="Token">The single-use token from the confirmation email.</param>
public readonly record struct ConfirmEmailRequest(
    string Email,
    string Token);
