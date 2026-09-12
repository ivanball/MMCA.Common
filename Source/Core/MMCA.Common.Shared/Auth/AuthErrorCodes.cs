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
}
