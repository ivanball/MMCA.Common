using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Users.UseCases.TwoFactor;

/// <summary>
/// The shared regenerate-recovery-codes workflow: prove a live second factor, then replace the whole
/// recovery-code set and return the new plaintext once.
/// </summary>
/// <remarks>
/// <para>
/// A replace, never an append. Regenerating exists to invalidate a list the user believes is lost or
/// copied, and a set that grew instead of turning over would leave the compromised codes live.
/// </para>
/// <para>
/// A recovery code may itself satisfy the challenge, which is the ordinary "I used one, print a fresh
/// list" flow: the presented code is spent by the challenge and then the whole set is replaced
/// anyway, so nothing survives the call.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The app's regenerate-recovery-codes command record.</typeparam>
/// <param name="authenticator">Runs the challenge.</param>
/// <param name="twoFactorService">Generates the replacement set.</param>
/// <param name="store">Replaces the stored hashes.</param>
/// <param name="logger">Logger for the audit line.</param>
public abstract class RegenerateRecoveryCodesHandlerBase<TCommand>(
    ITwoFactorAuthenticator authenticator,
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

        var challenge = await authenticator
            .ChallengeAsync(command.UserId, command.Request.Code, cancellationToken)
            .ConfigureAwait(false);
        if (challenge.IsFailure)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(challenge.Errors);
        }

        if (challenge.Value == TwoFactorOutcome.NotEnrolled)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(
                TwoFactorErrors.TwoFactorNotEnrolled(HandlerName));
        }

        var recoveryCodes = twoFactorService.GenerateRecoveryCodes();

        var replaced = await store
            .ReplaceRecoveryCodesAsync(command.UserId, recoveryCodes.Hashes, cancellationToken)
            .ConfigureAwait(false);
        if (replaced.IsFailure)
        {
            return Result.Failure<TwoFactorRecoveryCodesResponse>(replaced.Errors);
        }

        UserUseCaseLog.TwoFactorRecoveryCodesRegenerated(logger, command.UserId);

        return Result.Success(new TwoFactorRecoveryCodesResponse(recoveryCodes.Codes));
    }
}
