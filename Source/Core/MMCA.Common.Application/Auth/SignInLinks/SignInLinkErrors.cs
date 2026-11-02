using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.SignInLinks;

/// <summary>
/// The failures the sign-in-link workflow returns, declared once so the redeem handler, the
/// authentication service and a client all match on the same literals.
/// </summary>
public static class SignInLinkErrors
{
    /// <summary>Code returned when a sign-in link is unknown, expired, mismatched, spent or disabled.</summary>
    public const string InvalidLinkCode = "Authentication.InvalidSignInLink";

    /// <summary>Code returned when the account has a second factor, which a link cannot satisfy.</summary>
    public const string SecondFactorRequiredCode = "Authentication.SignInLinkSecondFactorRequired";

    /// <summary>Code returned by an authentication service that has not adopted sign-in links.</summary>
    public const string NotSupportedCode = "Authentication.SignInLinkNotSupported";

    /// <summary>The single rejection every failing redemption collapses to.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>An unauthorized error.</returns>
    public static Error InvalidLink(string? source = null) => Error.Unauthorized(
        InvalidLinkCode,
        "The sign-in link is invalid or has expired. Please request a new one.",
        source);

    /// <summary>
    /// The refusal for an account that has enrolled a second factor. A link proves only that the
    /// caller reads the mailbox, so honoring it would let the mailbox alone bypass the second factor.
    /// </summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>An unauthorized error.</returns>
    /// <remarks>Reachable only after a valid token was spent, so it tells the mailbox owner, not a sweeper.</remarks>
    public static Error SecondFactorRequired(string? source = null) => Error.Unauthorized(
        SecondFactorRequiredCode,
        "This account uses two-factor authentication. Sign in with your password and code instead.",
        source);

    /// <summary>The refusal an authentication service returns when it has not adopted sign-in links.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>A failure error.</returns>
    public static Error NotSupported(string? source = null) => Error.Failure(
        NotSupportedCode,
        "Sign-in links are not supported.",
        source);
}
