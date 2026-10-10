using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.Administration;

/// <summary>
/// The narrow surface <c>UsersAdminControllerBase</c> talks to: read, lock and re-role user
/// accounts. The consumer implements it over its own <c>User</c> aggregate. The roster itself is
/// listed through the app's generic paged entity endpoint, not through this service.
/// </summary>
/// <remarks>
/// <para>
/// A service rather than three command/query handler bases, because every member here is one straight
/// translation of a request into the app's own store and none of them needs the decorator pipeline's
/// caching, transaction or validation behavior. The controller base gates access with
/// <c>[HasPermission]</c>, so the capability check happens at the transport boundary the way it does
/// for the rest of the API package's shipped controllers.
/// </para>
/// <para>
/// The DTO stays the app's own (<typeparamref name="TUserDto"/>): the fields an operator needs to see
/// on a user differ per app, and a framework-owned shape would either be too thin to use or would
/// force every app to carry fields it has no column for.
/// </para>
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
public interface IUserAdministrationService<TUserDto>
{
    /// <summary>
    /// Returns one account, or a not-found failure.
    /// </summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account.</returns>
    Task<Result<TUserDto>> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks or unlocks an account.
    /// </summary>
    /// <remarks>
    /// An implementation that locks an account should also revoke its live refresh sessions, or the
    /// lock only takes effect when the current access token expires. <c>IRefreshSessionStore</c> and
    /// <c>RefreshSessionRevocation.RevokeAllAsync</c> are what the framework's own credential-rotation
    /// paths use for exactly this.
    /// </remarks>
    /// <param name="userId">The account.</param>
    /// <param name="locked"><see langword="true"/> to lock, <see langword="false"/> to unlock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the app's failure.</returns>
    Task<Result> SetLockedAsync(UserIdentifierType userId, bool locked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the set of roles an account holds.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="roles">The complete set of roles the account should hold afterwards.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the app's failure.</returns>
    Task<Result> SetRolesAsync(
        UserIdentifierType userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default);
}
