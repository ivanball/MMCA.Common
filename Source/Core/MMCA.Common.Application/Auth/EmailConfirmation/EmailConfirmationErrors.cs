using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.EmailConfirmation;

/// <summary>
/// The failures the email-confirmation workflow returns, declared once so the sign-in gate, the
/// confirm handler and a client all match on the same literals.
/// </summary>
public static class EmailConfirmationErrors
{
    /// <summary>Code returned at sign-in when the account's address has not been confirmed.</summary>
    public const string EmailNotConfirmedCode = "Authentication.EmailNotConfirmed";

    /// <summary>Code returned when a confirmation token is unknown, expired, mismatched or spent.</summary>
    public const string InvalidTokenCode = "Authentication.InvalidConfirmationToken";

    /// <summary>The sign-in refusal for an unconfirmed address.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>An unauthorized error.</returns>
    /// <remarks>
    /// Distinct from <c>Auth.InvalidCredentials</c> so the UI can offer to resend the link, and
    /// reachable only after the password has been proved, so it tells the account owner rather than
    /// an address sweeper.
    /// </remarks>
    public static Error EmailNotConfirmed(string? source = null) => Error.Unauthorized(
        EmailNotConfirmedCode,
        "Confirm your email address before signing in. Request a new confirmation link if you no longer have it.",
        source);

    /// <summary>The single rejection every failing redemption collapses to.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>An unauthorized error.</returns>
    public static Error InvalidToken(string? source = null) => Error.Unauthorized(
        InvalidTokenCode,
        "The confirmation link is invalid or has expired. Please request a new one.",
        source);
}
