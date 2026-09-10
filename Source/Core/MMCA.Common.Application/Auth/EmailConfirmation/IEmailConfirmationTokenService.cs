using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.EmailConfirmation;

/// <summary>
/// Issues and redeems the single-use tokens behind the email-confirmation workflow, on the same
/// design as <see cref="IPasswordResetTokenService"/>: the token material lives outside the database,
/// hashed at rest, with a per-address request throttle and a per-token validation-attempt cap.
/// </summary>
/// <remarks>
/// A separate service rather than a second purpose bolted onto the reset one, because the two have
/// different lifetimes (minutes against a day) and must not share a record: issuing a confirmation
/// link would otherwise silently invalidate an outstanding password-reset link for the same address.
/// </remarks>
public interface IEmailConfirmationTokenService
{
    /// <summary>
    /// Issues a confirmation token for <paramref name="email"/>, replacing any token already
    /// outstanding for that address (one active token per address).
    /// </summary>
    /// <param name="email">The address the token is issued for. Normalized by the implementation.</param>
    /// <param name="userId">The account the token resolves back to when it is redeemed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A success result carrying the raw token to email, or a failure when the per-address request
    /// throttle has been exceeded.
    /// </returns>
    Task<Result<string>> IssueAsync(string email, UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates <paramref name="token"/> against the outstanding token for <paramref name="email"/>
    /// and consumes it on success, so a token never redeems twice.
    /// </summary>
    /// <param name="email">The address the token was issued for.</param>
    /// <param name="token">The raw token supplied by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A success result carrying the account the token belongs to, or a single generic failure
    /// (unknown, expired, mismatched and attempt-capped all collapse to the same error).
    /// </returns>
    Task<Result<UserIdentifierType>> ValidateAndConsumeAsync(string email, string token, CancellationToken cancellationToken = default);
}
