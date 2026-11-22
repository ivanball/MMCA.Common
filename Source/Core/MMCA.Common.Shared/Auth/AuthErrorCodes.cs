namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Error codes the framework's authentication surface returns, named once so a caller that has to
/// react to a specific outcome (rather than only display its message) compares against a constant
/// instead of repeating the literal.
/// </summary>
/// <remarks>
/// The code, not the message, is the contract: messages are localized and may be reworded, while a
/// code is matched with <see cref="System.StringComparison.Ordinal"/> by UI and API callers alike.
/// </remarks>
public static class AuthErrorCodes
{
    /// <summary>
    /// Registration was refused because the address already belongs to an account. Both the up-front
    /// check and the unique-index race recovery return it, so the two paths stay indistinguishable to
    /// the caller. The registration UI keys its "sign in instead" guidance on this code.
    /// </summary>
    public const string EmailAlreadyExists = "Auth.EmailAlreadyExists";

    /// <summary>
    /// Registration was refused because the host requires acceptance of its Terms of Service and the
    /// request did not carry it. Returned only when a current terms version is configured.
    /// </summary>
    public const string TermsNotAccepted = "Auth.TermsNotAccepted";

    /// <summary>
    /// The request was refused because the operation needs a session that signed in with a second
    /// factor and the caller's token carries none (no <see cref="AuthClaimTypes.MultiFactor"/> claim).
    /// Returned by the CQRS gate for a use case that demands a step-up and by the HTTP permission
    /// check for a role the host lists under <c>Authorization:MultiFactor:RequiredForRoles</c>. A
    /// client reacts by sending the user to set up two-factor authentication or to sign in again with
    /// a code.
    /// </summary>
    public const string MultiFactorRequired = "Authorization.MultiFactorRequired";

    /// <summary>
    /// Sign-in was refused because the account has a second factor and the request carried no code.
    /// The password has been proved at that point: a client prompts for the code and retries the same
    /// credentials with it.
    /// </summary>
    public const string TwoFactorRequired = "Authentication.TwoFactorRequired";

    /// <summary>
    /// Sign-in (or a two-factor action) was refused because the supplied second-factor code did not
    /// verify. A client keeps the code step and asks for a new code.
    /// </summary>
    public const string TwoFactorInvalid = "Authentication.TwoFactorInvalid";

    /// <summary>
    /// Sign-in was refused because the host requires a confirmed address and the account's address has
    /// not been confirmed. A client offers to send the confirmation link again.
    /// </summary>
    public const string EmailNotConfirmed = "Authentication.EmailNotConfirmed";
}
