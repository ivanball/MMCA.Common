using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Users.UseCases.TwoFactor;

/// <summary>
/// The shared confirm-two-factor-enrollment workflow: verify a code minted from the pending secret,
/// activate the second factor, and hand back the account's first set of single-use recovery codes.
/// </summary>
/// <remarks>
/// <para>
/// The recovery codes are generated and stored in the SAME call that activates the factor, so an
/// account can never be in the state "second factor on, no way back in". They are returned in
/// plaintext exactly once; the store keeps only hashes.
/// </para>
/// <para>
/// Verification here is deliberately time-based only. A recovery code cannot confirm an enrollment,
/// because the whole point of the step is proving the authenticator app really holds the secret.
/// </para>
/// <para>
/// SECURITY (M228): an account whose factor is already active is refused, because the secret stays
/// stored after enrollment and a time-based code alone would otherwise mint a fresh recovery set.
/// The code is verified through <see cref="ITwoFactorAuthenticator.VerifyEnrollmentCodeAsync"/>, so
/// its time step is accepted once, and a wrong code counts against the per-account second-factor
/// budget the same way a wrong current password counts in the change-password workflow.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The app's confirm-enrollment command record.</typeparam>
/// <param name="twoFactorService">Generates the recovery codes.</param>
/// <param name="authenticator">Verifies the code under the single-use time-step guard.</param>
/// <param name="store">Reads the pending secret and activates the factor.</param>
/// <param name="loginProtection">The failed-attempt counter and exponential lockout (ADR-029).</param>
/// <param name="logger">Logger for the enrollment audit line.</param>
public abstract partial class ConfirmTwoFactorEnrollmentHandlerBase<TCommand>(
    ITwoFactorService twoFactorService,
    ITwoFactorAuthenticator authenticator,
    ITwoFactorStore store,
    ILoginProtectionService loginProtection,
    ILogger logger) : ICommandHandler<TCommand, Result<TwoFactorRecoveryCodesResponse>>
    where TCommand : IUserScopedCommand<TwoFactorCodeRequest>
{
    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result<TwoFactorRecoveryCodesResponse>> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var state = await store.GetAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (state?.TwoFactorSecret is not { Length: > 0 } secret)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(
                TwoFactorErrors.TwoFactorEnrollmentMissing(HandlerName));
        }

        if (state.IsTwoFactorEnabled)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(Error.Conflict(
                "Authentication.TwoFactorAlreadyEnabled",
                "Two-factor authentication is already enabled for this account. Disable it before enrolling again.",
                HandlerName));
        }

        var protectionKey = TwoFactorAttemptKey.For(command.UserId);
        var lockout = await loginProtection.CheckLockoutAsync(protectionKey, cancellationToken).ConfigureAwait(false);
        if (lockout.IsFailure)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(lockout.Errors);
        }

        var verified = await authenticator
            .VerifyEnrollmentCodeAsync(command.UserId, secret, command.Request.Code, cancellationToken)
            .ConfigureAwait(false);
        if (!verified)
        {
            await loginProtection.IncrementFailedAttemptsAsync(protectionKey, cancellationToken).ConfigureAwait(false);
            return Result.Failure<TwoFactorRecoveryCodesResponse>(TwoFactorErrors.TwoFactorInvalid(HandlerName));
        }

        await loginProtection.ResetFailedAttemptsAsync(protectionKey, cancellationToken).ConfigureAwait(false);

        var recoveryCodes = twoFactorService.GenerateRecoveryCodes();

        var completed = await store
            .CompleteEnrollmentAsync(command.UserId, recoveryCodes.Hashes, cancellationToken)
            .ConfigureAwait(false);
        if (completed.IsFailure)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(completed.Errors);
        }

        TwoFactorEnabled(logger, command.UserId);

        return Result.Success(new TwoFactorRecoveryCodesResponse(recoveryCodes.Codes));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Two-factor authentication enabled for user {UserId}")]
    private static partial void TwoFactorEnabled(ILogger logger, UserIdentifierType userId);
}
