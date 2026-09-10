using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Auth.TwoFactor;

/// <summary>
/// The shipped <see cref="ITwoFactorAuthenticator"/>: reads the account's state through
/// <see cref="ITwoFactorStore"/>, tries the time-based code first and a recovery code second, and
/// spends the recovery code it matched.
/// </summary>
/// <remarks>
/// <para>
/// Order matters. The time-based code is tried first because it is the ordinary path and costs one
/// HMAC; the recovery list is only walked when that fails, which is also what keeps a six-digit code
/// from ever being compared against the recovery hashes in the common case.
/// </para>
/// <para>
/// <b>The recovery code is spent before this returns.</b> Persisting the redemption is what makes the
/// code single use, so it happens inside the challenge rather than being left to the caller: a
/// caller that forgot would leave the code live for a second sign-in.
/// </para>
/// </remarks>
/// <param name="twoFactorService">Verifies codes and matches recovery hashes.</param>
/// <param name="store">Reads the account's state and spends recovery codes.</param>
internal sealed class TwoFactorAuthenticator(
    ITwoFactorService twoFactorService,
    ITwoFactorStore store) : ITwoFactorAuthenticator
{
    /// <inheritdoc />
    public async Task<Result<TwoFactorOutcome>> ChallengeAsync(
        UserIdentifierType userId,
        string? code,
        CancellationToken cancellationToken = default)
    {
        var state = await store.GetAsync(userId, cancellationToken).ConfigureAwait(false);

        // An account that never enrolled, and an account whose row vanished between the password
        // check and here, are both "nothing to challenge". The caller has already decided the account
        // exists, so this is not the place to turn a race into a different error.
        if (state is null || !state.IsTwoFactorEnabled)
        {
            return Result.Success(TwoFactorOutcome.NotEnrolled);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Result.Failure<TwoFactorOutcome>(
                TwoFactorErrors.TwoFactorRequired(nameof(ChallengeAsync)));
        }

        if (state.TwoFactorSecret is { Length: > 0 } secret && twoFactorService.VerifyCode(secret, code))
        {
            return Result.Success(TwoFactorOutcome.VerifiedTotp);
        }

        if (!twoFactorService.TryMatchRecoveryCode(code, state.TwoFactorRecoveryCodeHashes, out var matchedHash)
            || matchedHash is null)
        {
            return Result.Failure<TwoFactorOutcome>(
                TwoFactorErrors.TwoFactorInvalid(nameof(ChallengeAsync)));
        }

        var consumed = await store
            .ConsumeRecoveryCodeAsync(userId, matchedHash, cancellationToken)
            .ConfigureAwait(false);

        // A redemption that could not be persisted is answered as a failed challenge, not a
        // successful one: letting the sign-in through would hand out a code that is still live.
        return consumed.IsFailure
            ? Result.Failure<TwoFactorOutcome>(consumed.Errors)
            : Result.Success(TwoFactorOutcome.VerifiedRecoveryCode);
    }
}
