using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

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
public abstract class ChangePasswordHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILogger logger) : ICommandHandler<TCommand, Result>
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
            UserUseCaseLog.PasswordChanged(logger, command.UserId);
        }

        return result;
    }
}
