using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Users.UseCases.EmailConfirmation;

/// <summary>
/// The shared complete-an-email-confirmation workflow: redeem the single-use token, let the aggregate
/// mark itself confirmed, and persist.
/// </summary>
/// <remarks>
/// <para>
/// Every rejection collapses to one <c>Authentication.InvalidConfirmationToken</c> error: an unknown,
/// expired, mismatched or attempt-capped token and a vanished account are indistinguishable to the
/// caller, so the endpoint reveals nothing about which addresses hold accounts or which tokens exist.
/// </para>
/// <para>
/// The token is consumed BEFORE the save, matching <c>ResetPasswordHandlerBase</c>: leaving it live
/// until the write succeeds opens a replay window in which the same token redeems twice, and a token
/// burned by a later invariant failure costs the user one more confirmation email.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's confirm-email command record.</typeparam>
/// <param name="unitOfWork">The unit of work the user aggregate is loaded and saved through.</param>
/// <param name="tokenService">Redeems the single-use confirmation token.</param>
/// <param name="logger">Logger for the confirmation audit lines.</param>
public abstract class ConfirmEmailHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    IEmailConfirmationTokenService tokenService,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IEmailConfirmableUser
    where TCommand : ICommandWithRequest<ConfirmEmailRequest>
{
    /// <summary>The unit of work (exposed for app-level extensions).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var request = command.Request;

        var consumed = await tokenService
            .ValidateAndConsumeAsync(request.Email, request.Token, cancellationToken)
            .ConfigureAwait(false);
        if (consumed.IsFailure)
        {
            UserUseCaseLog.EmailConfirmationRejected(logger, "token rejected");
            return Result.Failure(EmailConfirmationErrors.InvalidToken(HandlerName));
        }

        var userId = consumed.Value;
        var repository = unitOfWork.GetRepository<TUser, UserIdentifierType>();
        var user = await repository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            UserUseCaseLog.EmailConfirmationRejected(logger, "account no longer resolvable");
            return Result.Failure(EmailConfirmationErrors.InvalidToken(HandlerName));
        }

        // Already confirmed is a success that writes nothing: the caller's request is satisfied, and
        // a confirmation link opened twice (a mail client prefetch, a double tap) is the most ordinary
        // duplicate this feature has.
        if (user.IsEmailConfirmed)
        {
            UserUseCaseLog.EmailConfirmed(logger, userId);
            return Result.Success();
        }

        var result = user.ConfirmEmail();
        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        UserUseCaseLog.EmailConfirmed(logger, userId);
        return result;
    }
}
