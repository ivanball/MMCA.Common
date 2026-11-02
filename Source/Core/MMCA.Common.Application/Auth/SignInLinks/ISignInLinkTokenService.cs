using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.SignInLinks;

/// <summary>
/// Issues and redeems the single-use tokens behind sign-in links (ADR-116), on the design
/// <see cref="EmailConfirmation.IEmailConfirmationTokenService"/> uses: the token material lives
/// outside the database, hashed at rest, with a per-address request throttle and a per-token
/// validation-attempt cap.
/// </summary>
/// <remarks>
/// A token is keyed by address and <see cref="SignInLinkPurpose"/>, NOT by account: an invitation is
/// issued to an address that may have no account yet, and redeeming it is what creates one. One
/// active token per address AND purpose, so asking for a sign-in link does not silently invalidate
/// an outstanding invitation for the same address.
/// </remarks>
public interface ISignInLinkTokenService
{
    /// <summary>
    /// Issues a sign-in token for <paramref name="email"/>, replacing any token of the same
    /// <paramref name="purpose"/> already outstanding for that address.
    /// </summary>
    /// <param name="email">The address the token is issued for. Normalized by the implementation.</param>
    /// <param name="purpose">Why the link is sent; selects its lifetime.</param>
    /// <param name="profile">
    /// The name to create the account with when the address has none yet. Supplied only for an
    /// invitation; a plain sign-in link never creates an account.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A success result carrying the raw token to email, or a failure when the per-address request
    /// throttle has been exceeded.
    /// </returns>
    Task<Result<string>> IssueAsync(
        string email,
        SignInLinkPurpose purpose,
        SignInLinkProfile? profile = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="token"/> against the outstanding tokens for <paramref name="email"/>
    /// and consumes the one it matches, so a token never redeems twice.
    /// </summary>
    /// <param name="email">The address the token was issued for.</param>
    /// <param name="token">The raw token supplied by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A success result carrying what the token grants, or a single generic failure (unknown,
    /// expired, mismatched and attempt-capped all collapse to the same error).
    /// </returns>
    Task<Result<SignInLinkGrant>> ValidateAndConsumeAsync(
        string email,
        string token,
        CancellationToken cancellationToken = default);
}

/// <summary>Why a sign-in link was sent. Each purpose has its own lifetime and its own active token.</summary>
public enum SignInLinkPurpose
{
    /// <summary>The account owner asked for a link from the sign-in page. Short-lived.</summary>
    SignIn = 0,

    /// <summary>An organizer invited the address. Long-lived, and may create the account.</summary>
    Invite = 1,
}

/// <summary>The name an invitation creates the account with, when the address has no account yet.</summary>
/// <param name="FirstName">The invitee's first name.</param>
/// <param name="LastName">The invitee's last name.</param>
public sealed record SignInLinkProfile(string FirstName, string LastName);

/// <summary>What a redeemed sign-in token grants.</summary>
/// <param name="Purpose">Why the link was sent.</param>
/// <param name="Profile">The provisioning name, when the link was an invitation that carried one.</param>
public sealed record SignInLinkGrant(SignInLinkPurpose Purpose, SignInLinkProfile? Profile);
