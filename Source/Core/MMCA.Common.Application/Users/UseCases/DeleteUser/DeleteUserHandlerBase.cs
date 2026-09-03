using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Users.UseCases.DeleteUser;

/// <summary>
/// The shared account-erasure workflow: owner-or-privileged-role authorization, soft-delete the
/// account, irreversibly anonymize its personal data in place (ADR-005), persist, then run whatever
/// post-commit tail the app added. Keeping the row preserves cross-context scalar references and the
/// audit trail while still satisfying the GDPR/CCPA "delete within 30 days" erasure promise.
/// </summary>
/// <remarks>
/// <para>
/// Everything genuinely app-specific stays in the subclass:
/// <list type="bullet">
///   <item><see cref="HasDeletePrivilege"/> - the role that bypasses ownership (Organizer vs Admin).</item>
///   <item><see cref="OnAfterSoftDeleteAsync"/> - the app's tail. It runs after <c>Delete()</c> and
///     before <c>Anonymize()</c>, which is the only point where an app can both read personal data
///     that anonymization is about to erase (ADC captures the avatar blob name) and enlist further
///     aggregates in the same unit of work (Store cascades to its linked <c>Customer</c>). Work that
///     must wait for the commit is enqueued on the <c>afterCommit</c> collection instead of being run
///     inline, so the override can hand values it captured here to a post-commit closure without
///     parking them in mutable handler state.</item>
/// </list>
/// </para>
/// <para>
/// The hook deliberately runs <b>after</b> <c>user.Delete()</c> rather than before it, so an
/// already-deleted account still fails with the account's own <c>AlreadyDeleted</c> error rather than
/// with an error raised by a cascaded aggregate.
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's delete-user command record.</typeparam>
public abstract class DeleteUserHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IErasableUser
    where TCommand : IUserOwnedRequest
{
    /// <summary>The unit of work (exposed so a subclass hook can enlist further aggregates).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass that keeps the pre-hoist class name
    /// (<c>DeleteUserHandler</c>) reports the identical error payload it did before.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Authorization: owner or the app's privileged role (case-insensitive; claims may carry any casing).
        var forbidden = UserOwnershipRule.CheckOwnership(
            command,
            HasDeletePrivilege(command.CurrentUserRole),
            code: "User.DeleteForbidden",
            message: "You can only delete your own account.",
            source: HandlerName);
        if (forbidden is not null)
        {
            return Result.Failure(forbidden);
        }

        var repository = unitOfWork.GetRepository<TUser, UserIdentifierType>();
        var user = await repository.GetByIdAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TUser).Name));
        }

        // Soft-delete (sets IsDeleted = true, plus whatever the aggregate couples to deletion).
        //
        // Outstanding refresh sessions are not revoked here and do not need to be: the refresh flow
        // re-fetches the user through the same soft-delete query filter, so an erased account's
        // sessions stop working the moment this commits. An app that also wants the rows tidied
        // revokes them from its OnAfterSoftDeleteAsync tail via IRefreshSessionStore, which enlists
        // in this same unit of work.
        //
        // Called through IErasableUser deliberately, and the cast is load-bearing: member lookup on a
        // type parameter prefers the members of its CLASS constraint, so a bare user.Delete() would
        // bind to AuditableBaseEntity<TId>.Delete() and a User that HIDES that method
        // (public new Result Delete(), as ADC's does) would have its own version skipped. Interface
        // dispatch resolves to the member the app type actually maps onto IErasableUser.
        IErasableUser erasable = user;
        var deleteResult = erasable.Delete();
        if (deleteResult.IsFailure)
        {
            return deleteResult;
        }

        var afterCommit = new List<Func<CancellationToken, Task>>();
        var tailResult = await OnAfterSoftDeleteAsync(user, command, afterCommit, cancellationToken).ConfigureAwait(false);
        if (tailResult.IsFailure)
        {
            return tailResult;
        }

        // Irreversibly erase the personal data on the deletion request (anonymize-in-place, ADR-005).
        var anonymizeResult = erasable.Anonymize();
        if (anonymizeResult.IsFailure)
        {
            return anonymizeResult;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var action in afterCommit)
        {
            await action(cancellationToken).ConfigureAwait(false);
        }

        UserUseCaseLog.UserErased(logger, command.UserId);

        return Result.Success();
    }

    /// <summary>
    /// Whether the caller's role bypasses the ownership requirement (e.g.
    /// <c>UserRole.IsOrganizer(currentUserRole)</c> for ADC, <c>UserRole.IsAdmin(...)</c> for Store).
    /// </summary>
    /// <param name="currentUserRole">The caller's role claim; may be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the caller may delete any account.</returns>
    protected abstract bool HasDeletePrivilege(string? currentUserRole);

    /// <summary>
    /// The app's erasure tail, invoked after the account was soft-deleted and before it is anonymized
    /// (default: nothing). Returning a failure aborts the erasure before anything is persisted.
    /// </summary>
    /// <param name="user">The tracked user being erased; its personal data is still intact here.</param>
    /// <param name="command">The originating command.</param>
    /// <param name="afterCommit">
    /// Actions to run, in order, once the erasure has been committed. Use this for side effects that
    /// must not happen if the save fails (deleting a blob, writing a cache marker); a post-commit
    /// action owns its own failure handling, since the erasure has already succeeded by then.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the failure that aborts the erasure.</returns>
    protected virtual Task<Result> OnAfterSoftDeleteAsync(
        TUser user,
        TCommand command,
        ICollection<Func<CancellationToken, Task>> afterCommit,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
