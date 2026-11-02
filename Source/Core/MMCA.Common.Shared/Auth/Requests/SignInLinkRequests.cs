namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for asking that a single-use sign-in link be emailed to an address.
/// </summary>
/// <remarks>
/// Anonymous, and answered identically whether or not the address holds an account, for the same
/// anti-enumeration reason the forgot-password endpoint is.
/// </remarks>
/// <param name="Email">The address to send the sign-in link to.</param>
public readonly record struct SendSignInLinkRequest(string Email);

/// <summary>
/// Request payload for redeeming a single-use sign-in link.
/// </summary>
/// <param name="Email">The address the link was issued for.</param>
/// <param name="Token">The single-use token from the link.</param>
/// <param name="AcceptedTerms">
/// Whether the visitor accepted the Terms of Service on the redeem page. Read only when redeeming the
/// link creates the account (an invitation to an address with no account yet), so the new account
/// carries the same acceptance a registration would.
/// </param>
public readonly record struct RedeemSignInLinkRequest(
    string Email,
    string Token,
    bool AcceptedTerms = false);
