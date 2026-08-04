using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Users.UseCases.ChangePreferences;

/// <summary>
/// The shared preference-write workflow (ADR-027 culture / ADR-028 theme). Each request field defaults
/// to the stored value when <see langword="null"/>, so updating one preference never clears the other.
/// The app Identity modules carried line-identical copies of this handler.
/// </summary>
/// <remarks>
/// The command record stays app-side (<typeparamref name="TCommand"/>) because ADC marks it
/// <c>ICacheInvalidating</c> and Store does not; the payload record
/// (<see cref="ChangePreferencesRequest"/>) was byte-identical in both apps and is now shared.
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TCommand">The app's change-preferences command record.</typeparam>
public abstract class ChangePreferencesHandlerBase<TUser, TCommand>(
    IUnitOfWork unitOfWork,
    ILogger logger) : ICommandHandler<TCommand, Result>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IUserPreferences
    where TCommand : IUserScopedCommand<ChangePreferencesRequest>
{
    /// <summary>The unit of work (exposed for app-level extensions).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass that keeps the pre-hoist class name
    /// (<c>ChangePreferencesHandler</c>) reports the identical error payload it did before.
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

        var result = user.UpdatePreferences(
            command.Request.Culture ?? user.PreferredCulture,
            command.Request.Theme ?? user.PreferredTheme);
        if (result.IsSuccess)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            UserUseCaseLog.PreferencesChanged(logger, command.UserId);
        }

        return result;
    }
}
