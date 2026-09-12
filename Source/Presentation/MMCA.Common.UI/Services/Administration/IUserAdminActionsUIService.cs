using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The three account actions the framework's <c>Admin/Users</c> endpoints expose (ADR-116): lock an
/// account out of sign-in, unlock it, and replace the roles it holds. Non-generic on purpose, so a
/// component that only acts on an account never has to name the app's DTO.
/// </summary>
/// <remarks>
/// Every member answers with a <see cref="Result"/> carrying the API's own errors and their
/// <see cref="ErrorType"/> intact, so a page branches on the outcome instead of catching. The
/// framework registers one implementation for both this contract and
/// <see cref="IUserAdminUIService{TUserDto}"/>; see <c>AddUserAdministrationUI</c>.
/// </remarks>
public interface IUserAdminActionsUIService
{
    /// <summary>Locks an account out of sign-in and revokes its live sessions.</summary>
    /// <param name="userId">The account to lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's own refusal.</returns>
    Task<Result> LockAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>Unlocks an account so it can sign in again.</summary>
    /// <param name="userId">The account to unlock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's own refusal.</returns>
    Task<Result> UnlockAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the set of roles an account holds.</summary>
    /// <param name="userId">The account to re-role.</param>
    /// <param name="roles">The roles the account should hold afterwards.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's own refusal.</returns>
    Task<Result> SetRolesAsync(
        UserIdentifierType userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the single role an account holds, by sending a one-element set to the role-replacement
    /// endpoint. Both shipped consumers hold exactly one role per account, so this is the shape
    /// their pages call.
    /// </summary>
    /// <remarks>
    /// Declared as its own member rather than a default interface implementation over
    /// <see cref="SetRolesAsync"/>: a default implementation is not virtual on the interface a mock
    /// proxies, so a test could neither stub nor verify it.
    /// </remarks>
    /// <param name="userId">The account to re-role.</param>
    /// <param name="role">The role the account should hold afterwards.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's own refusal.</returns>
    Task<Result> SetRoleAsync(UserIdentifierType userId, string role, CancellationToken cancellationToken = default);
}
