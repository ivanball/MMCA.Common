using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Users.UseCases.TwoFactor;

/// <summary>
/// The shared disable-two-factor workflow: prove a live second factor, then clear the account's
/// secret and recovery codes.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: a code is demanded to turn the factor OFF, not only to turn it on. Without that, whoever
/// holds a stolen access token can strip the very control that would have stopped them, and the
/// second factor protects nothing beyond the sign-in that minted the token.
/// </para>
/// <para>
/// A recovery code is accepted here (unlike at enrollment), because a user who has lost the
/// authenticator device is exactly the person who needs to disable it, and spending one recovery code
/// to do so is the intended use of the list.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The app's disable-two-factor command record.</typeparam>
/// <param name="authenticator">Runs the challenge, spending a recovery code when one is presented.</param>
/// <param name="store">Clears the account's two-factor state.</param>
/// <param name="logger">Logger for the audit line.</param>
public abstract class DisableTwoFactorHandlerBase<TCommand>(
    ITwoFactorAuthenticator authenticator,
    ITwoFactorStore store,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TCommand : IUserScopedCommand<TwoFactorCodeRequest>
{
    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var challenge = await authenticator
            .ChallengeAsync(command.UserId, command.Request.Code, cancellationToken)
            .ConfigureAwait(false);
        if (challenge.IsFailure)
        {
            return Result.Failure(challenge.Errors);
        }

        // The challenge answers NotEnrolled for an account with no active factor. Disabling one that
        // does not exist is reported rather than treated as an idempotent success, because the caller
        // just presented a code and deserves to know it was never checked against anything.
        if (challenge.Value == TwoFactorOutcome.NotEnrolled)
        {
            return Result.Failure(TwoFactorErrors.TwoFactorNotEnrolled(HandlerName));
        }

        var disabled = await store.DisableAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (disabled.IsFailure)
        {
            return disabled;
        }

        UserUseCaseLog.TwoFactorDisabled(logger, command.UserId);

        return Result.Success();
    }
}
