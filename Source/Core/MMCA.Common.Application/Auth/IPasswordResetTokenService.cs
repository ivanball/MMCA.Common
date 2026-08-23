using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Issues and redeems the single-use tokens behind the forgot-password workflow. Implementations
/// keep the token material outside the database (cache-backed, hashed at rest) and enforce the
/// per-email request throttle and the per-token validation-attempt cap.
/// </summary>
public interface IPasswordResetTokenService
{
    /// <summary>
    /// Issues a reset token for <paramref name="email"/>, replacing any token already outstanding for
    /// that address (one active token per email).
    /// </summary>
    /// <param name="email">The address the token is issued for. Normalized by the implementation.</param>
    /// <param name="userId">The account the token resolves back to when it is redeemed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A success result carrying the raw token to email, or a failure when the per-email request
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
