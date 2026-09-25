using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// The two anonymous email-confirmation calls (ADR-116) behind the shared <c>/confirm-email</c>
/// page: redeem a link's single-use token, and ask for a fresh link when the old one has expired.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous by contract: the confirmation link is followed from an email client with no session,
/// so an implementation must not depend on a credential being present. Every member returns a
/// <see cref="Result"/> carrying the API's own errors with their <see cref="ErrorType"/> intact.
/// </para>
/// <para>
/// <b>No retry.</b> The token is single-use and the resend spends a per-address request budget, so a
/// repeated request is a second request rather than a replayed response: a transient failure is
/// reported to the visitor, who can press the button again.
/// </para>
/// </remarks>
public interface IEmailConfirmationUIService
{
    /// <summary>Redeems a confirmation token via <c>POST auth/confirm-email</c>.</summary>
    /// <param name="email">The address the link was sent to.</param>
    /// <param name="token">The single-use token from the link or the message body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the single generic rejection the endpoint returns for every bad token.</returns>
    Task<Result> ConfirmEmailAsync(string email, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for a fresh confirmation link via <c>POST auth/send-email-confirmation</c>. The endpoint
    /// answers 202 whether or not the address holds an account, so a success means "accepted", never
    /// "this address exists".
    /// </summary>
    /// <param name="email">The address to send the link to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success on any well-formed request.</returns>
    Task<Result> ResendEmailConfirmationAsync(string email, CancellationToken cancellationToken = default);
}
