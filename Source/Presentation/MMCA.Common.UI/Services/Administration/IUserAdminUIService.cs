using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The reading half of user administration (ADR-116) over the app's own administration DTO: one
/// account and its live sessions, from the <c>Admin/Users</c> endpoints the framework's
/// <c>UsersAdminControllerBase</c> serves. Inherits the three account actions from
/// <see cref="IUserAdminActionsUIService"/>.
/// </summary>
/// <remarks>
/// The roster is not listed here: it is the app's generic paged entity endpoint, read through the
/// standard entity service and handed to <c>UserAdminList</c> as its <c>FetchPage</c> delegate.
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
public interface IUserAdminUIService<TUserDto> : IUserAdminActionsUIService
{
    /// <summary>Reads one account, including its lock state, from <c>GET Admin/Users/{userId}</c>.</summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account, or the API's own refusal.</returns>
    Task<Result<TUserDto>> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the account's live (signed-in) sessions, newest first, from
    /// <c>GET Admin/Users/{userId}/sessions</c>. View only.
    /// </summary>
    /// <param name="userId">The account whose sessions to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live sessions, or the API's own refusal.</returns>
    Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);
}
