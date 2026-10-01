using System.Globalization;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Interfaces;
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
/// <para>
/// <b>A time-based code is accepted once.</b> The matched time step is remembered per account in
/// <see cref="ICacheService"/> for as long as the verification window can still accept it, and a
/// code whose step is not newer than the last accepted one is answered as invalid, so a code seen
/// over someone's shoulder cannot be replayed inside its window.
/// </para>
/// </remarks>
/// <param name="twoFactorService">Verifies codes and matches recovery hashes.</param>
/// <param name="store">Reads the account's state and spends recovery codes.</param>
/// <param name="cache">Remembers the last accepted time step per account.</param>
/// <param name="settings">The bound two-factor settings (period and window size).</param>
internal sealed class TwoFactorAuthenticator(
    ITwoFactorService twoFactorService,
    ITwoFactorStore store,
    ICacheService cache,
    IOptions<TwoFactorSettings> settings) : ITwoFactorAuthenticator
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

        if (state.TwoFactorSecret is { Length: > 0 } secret
            && twoFactorService.VerifyCode(secret, code, out var matchedStep))
        {
            return await AcceptTimeStepOnceAsync(userId, matchedStep, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Accepts a matched time step only when it is newer than the last one accepted for the account,
    /// then remembers it for as long as the window can still accept it.
    /// </summary>
    /// <param name="userId">The account being challenged.</param>
    /// <param name="matchedStep">The time step the presented code matched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see cref="TwoFactorOutcome.VerifiedTotp"/>, or an invalid-code failure for a replay.</returns>
    private async Task<Result<TwoFactorOutcome>> AcceptTimeStepOnceAsync(
        UserIdentifierType userId,
        long matchedStep,
        CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"twofactor:laststep:{userId}");

        // Shared-store read: a step accepted on another replica must be seen here, or a replayed code
        // would pass against a stale local copy inside its window.
        var last = await cache.GetFromSharedStoreAsync<long?>(key, cancellationToken).ConfigureAwait(false);
        if (last is { } lastStep && matchedStep <= lastStep)
        {
            return Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorInvalid(nameof(ChallengeAsync)));
        }

        var options = settings.Value;
        var lifetime = TimeSpan.FromSeconds(options.PeriodSeconds * (2 * options.VerificationWindowSteps + 2));
        await cache.SetAsync<long?>(key, matchedStep, lifetime, cancellationToken).ConfigureAwait(false);
        return Result.Success(TwoFactorOutcome.VerifiedTotp);
    }
}
