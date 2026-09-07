using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Users.UseCases.ChangePassword;

/// <summary>
/// The shared password-change workflow (ADR-032): verify the current password, hash the new one, let
/// the aggregate apply its own invariants, persist only on success. The app Identity modules carried
/// line-identical copies of this handler; only the log message text differed.
/// </summary>
/// <remarks>
/// The command record stays app-side (<typeparamref name="TCommand"/>): ADC marks it
/// <c>ICacheInvalidating</c> with a cache prefix built from its own <c>User</c> type and Store does
/// not, so a single shared record could not preserve both behaviors. The base reads the command only
/// through <see cref="IUserScopedCommand{TRequest}"/>.
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's change-password command record.</typeparam>
/// <param name="unitOfWork">The unit of work the user aggregate is loaded and saved through.</param>
/// <param name="passwordHasher">Verifies the current credential and derives the new one.</param>
/// <param name="logger">Logger for the change-password audit line.</param>
/// <param name="refreshSessions">
/// Optional refresh-session store. When supplied, a successful change revokes every live session on
/// the account (ADR-097), so a stolen refresh chain cannot survive the remediation the user just
/// performed. Optional and defaulted so an existing subclass keeps compiling; a host that wired
/// refresh sessions should pass it.
/// </param>
/// <param name="timeProvider">Optional clock used to stamp the revocation; defaults to the system clock.</param>
public abstract class ChangePasswordHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILogger logger,
    IRefreshSessionStore? refreshSessions = null,
    TimeProvider? timeProvider = null) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IPasswordChangeableUser
    where TCommand : IUserScopedCommand<ChangePasswordRequest>
{
    /// <summary>The unit of work (exposed for app-level extensions).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass that keeps the pre-hoist class name
    /// (<c>ChangePasswordHandler</c>) reports the identical error payload it did before.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var repository = unitOfWork.GetRepository<TUser, UserIdentifierType>();
        var user = await repository.GetByIdAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TUser).Name));
        }

        // SECURITY: an account with no stored credential material (an external-OAuth account,
        // ADR-036) has no current password to prove, so it has no change-password path either.
        // Rejecting it before the verify keeps a credential-less row from ever being treated as one
        // whose "current password" can be satisfied.
        if (user.PasswordHash.Length == 0 || user.PasswordSalt.Length == 0)
        {
            return Result.Failure(
                Error.Unauthorized("Auth.InvalidCurrentPassword", "Current password is incorrect.", HandlerName));
        }

        if (!passwordHasher.VerifyPassword(command.Request.CurrentPassword, user.PasswordHash, user.PasswordSalt))
        {
            return Result.Failure(
                Error.Unauthorized("Auth.InvalidCurrentPassword", "Current password is incorrect.", HandlerName));
        }

        var (newHash, newSalt) = passwordHasher.HashPassword(command.Request.NewPassword);
        var result = user.ChangePassword(newHash, newSalt);
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Credential rotation evicts every live refresh session: an attacker holding a stolen
            // chain must not keep minting tokens through the exact remediation the user performed.
            await RefreshSessionRevocation
                .RevokeAllAsync(refreshSessions, timeProvider, command.UserId, cancellationToken)
                .ConfigureAwait(false);

            UserUseCaseLog.PasswordChanged(logger, command.UserId);
        }

        return result;
    }
}
