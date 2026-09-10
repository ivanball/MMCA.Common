using Microsoft.Extensions.Logging;
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
/// </remarks>
/// <typeparam name="TCommand">The app's confirm-enrollment command record.</typeparam>
/// <param name="twoFactorService">Verifies the code and generates the recovery codes.</param>
/// <param name="store">Reads the pending secret and activates the factor.</param>
/// <param name="logger">Logger for the enrollment audit line.</param>
public abstract class ConfirmTwoFactorEnrollmentHandlerBase<TCommand>(
    ITwoFactorService twoFactorService,
    ITwoFactorStore store,
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

        if (!twoFactorService.VerifyCode(secret, command.Request.Code))
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(TwoFactorErrors.TwoFactorInvalid(HandlerName));
        }

        var recoveryCodes = twoFactorService.GenerateRecoveryCodes();

        var completed = await store
            .CompleteEnrollmentAsync(command.UserId, recoveryCodes.Hashes, cancellationToken)
            .ConfigureAwait(false);
        if (completed.IsFailure)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(completed.Errors);
        }

        UserUseCaseLog.TwoFactorEnabled(logger, command.UserId);

        return Result.Success(new TwoFactorRecoveryCodesResponse(recoveryCodes.Codes));
    }
}
