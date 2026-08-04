using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Users.UseCases.GetPreferences;

/// <summary>
/// The shared preference-read workflow (ADR-027 / ADR-028). The query and the response were
/// byte-identical in both app Identity modules and are now shared types, so the base is generic in the
/// <c>User</c> aggregate only.
/// </summary>
/// <remarks>
/// The lookup goes through <c>GetReadRepository</c>. The two app copies disagreed here (ADC read,
/// Store write) and the read repository is the correct choice for a query handler, which never calls
/// <c>SaveChangesAsync</c>: Store gains a no-tracking read on adoption.
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
public abstract class GetUserPreferencesHandlerBase<TUser>(IUnitOfWork unitOfWork)
    : IQueryHandler<GetUserPreferencesQuery, Result<UserPreferencesResponse>>
    where TUser : AuditableBaseEntity<UserIdentifierType>, IUserPreferences
{
    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass that keeps the pre-hoist class name
    /// (<c>GetUserPreferencesHandler</c>) reports the identical error payload it did before.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result<UserPreferencesResponse>> HandleAsync(
        GetUserPreferencesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var repository = unitOfWork.GetReadRepository<TUser, UserIdentifierType>();
        var user = await repository.GetByIdAsync(query.UserId, cancellationToken).ConfigureAwait(false);
        return user is null
            ? Result.Failure<UserPreferencesResponse>(
                Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TUser).Name))
            : Result.Success(new UserPreferencesResponse(user.PreferredCulture, user.PreferredTheme));
    }
}
