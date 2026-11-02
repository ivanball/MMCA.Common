using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// The client behind the sign-in-link flow (ADR-116): ask for a link from the sign-in page, and redeem
/// one on the <c>/auth/sign-in-link</c> landing page.
/// </summary>
/// <remarks>
/// A separate interface rather than new <see cref="IAuthUIService"/> members, so a consumer's own
/// <see cref="IAuthUIService"/> implementation keeps compiling (the <see cref="IEmailConfirmationUIService"/>
/// precedent). The framework's <see cref="AuthUIService"/> implements both, so a redeemed link stores
/// its tokens and signals the auth state exactly as a password sign-in does.
/// </remarks>
public interface ISignInLinkUIService
{
    /// <summary>
    /// Asks the API to email a sign-in link. The endpoint answers 202 for every well-formed address,
    /// so success says nothing about whether the address holds an account.
    /// </summary>
    /// <param name="email">The address to send the link to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's failure for a malformed request or an outage.</returns>
    Task<Result> RequestSignInLinkAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a sign-in link and, on success, stores the token pair and signals the signed-in state.
    /// Posts exactly once: the token is single-use, so a retry could only spend it twice.
    /// </summary>
    /// <param name="email">The address the link was issued for.</param>
    /// <param name="token">The single-use token from the link.</param>
    /// <param name="acceptedTerms">Whether the visitor accepted the Terms of Service on the page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token pair, or the API's refusal.</returns>
    Task<Result<AuthenticationResponse>> RedeemSignInLinkAsync(
        string email,
        string token,
        bool acceptedTerms,
        CancellationToken cancellationToken = default);
}
