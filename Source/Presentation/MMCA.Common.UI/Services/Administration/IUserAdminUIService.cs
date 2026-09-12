using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The reading half of user administration (ADR-116) over the app's own administration DTO: one
/// page of accounts and one account, from the <c>Admin/Users</c> endpoints the framework's
/// <c>UsersAdminControllerBase</c> serves. Inherits the three account actions from
/// <see cref="IUserAdminActionsUIService"/>.
/// </summary>
/// <remarks>
/// The listing is its own member rather than the generic entity-service <c>GetPagedAsync</c>: the
/// administration endpoint takes a search term and a role, not the grid's per-column filter syntax,
/// and it carries the pagination metadata in the body (the <c>X-Pagination</c> header repeats it),
/// so the flattening to <c>(Items, TotalItems)</c> happens here either way.
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
public interface IUserAdminUIService<TUserDto> : IUserAdminActionsUIService
{
    /// <summary>Reads one page of accounts from <c>GET Admin/Users/paged</c>.</summary>
    /// <param name="pageNumber">The 1-based page.</param>
    /// <param name="pageSize">Accounts per page.</param>
    /// <param name="searchTerm">Optional free-text filter; the server decides which fields it covers.</param>
    /// <param name="role">Optional role filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page and the total count, or the API's own refusal.</returns>
    Task<Result<(IReadOnlyList<TUserDto> Items, int TotalItems)>> GetPagedAsync(
        int pageNumber,
        int pageSize,
        string? searchTerm,
        string? role,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one account, including its lock state, from <c>GET Admin/Users/{userId}</c>.</summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account, or the API's own refusal.</returns>
    Task<Result<TUserDto>> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);
}
