using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.TwoFactor;

/// <summary>
/// The one step the sign-in workflow adds when an app adopts two-factor authentication: given an
/// account and whatever second-factor code the caller supplied, decide whether the sign-in may
/// proceed, and say which method satisfied it so the issued token can carry the right
/// <c>mfa</c> claim value.
/// </summary>
/// <remarks>
/// <para>
/// It is a separate collaborator, not extra members on <c>ITwoFactorStore</c>, because the sign-in
/// path needs exactly this decision and nothing else: <c>AuthenticationServiceBase</c> takes it as an
/// OPTIONAL constructor dependency, so an app that has not adopted two-factor passes nothing and its
/// sign-in behaves exactly as it did.
/// </para>
/// <para>
/// It runs AFTER the password check, for the reason the account-state gate does: reaching it proves
/// the caller owns the credential, so answering "this account needs a second factor" tells the owner
/// something rather than telling an address sweeper which accounts are protected.
/// </para>
/// </remarks>
public interface ITwoFactorAuthenticator
{
    /// <summary>
    /// Runs the second-factor challenge for one account.
    /// </summary>
    /// <param name="userId">The account whose password has just been proved.</param>
    /// <param name="code">The code the caller supplied, or <see langword="null"/> when none was sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="TwoFactorOutcome.NotEnrolled"/> when the account has no active second factor (the
    /// sign-in continues unchanged and the token carries no <c>mfa</c> claim), the verified method
    /// when a code satisfied the challenge, or a failure:
    /// <see cref="TwoFactorErrors.TwoFactorRequiredCode"/> when the account needs a code and none
    /// arrived, and <see cref="TwoFactorErrors.TwoFactorInvalidCode"/> when one arrived and did not
    /// verify.
    /// </returns>
    Task<Result<TwoFactorOutcome>> ChallengeAsync(
        UserIdentifierType userId,
        string? code,
        CancellationToken cancellationToken = default);
}

/// <summary>How a second-factor challenge was satisfied.</summary>
public enum TwoFactorOutcome
{
    /// <summary>The account has no active second factor, so nothing was challenged.</summary>
    NotEnrolled = 0,

    /// <summary>A time-based one-time code from the authenticator app verified.</summary>
    VerifiedTotp = 1,

    /// <summary>A single-use recovery code verified and was spent.</summary>
    VerifiedRecoveryCode = 2,
}
