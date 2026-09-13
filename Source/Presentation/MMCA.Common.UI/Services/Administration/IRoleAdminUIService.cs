using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The UI's view of the <c>Admin/Roles</c> endpoints the framework's <c>RolesAdminControllerBase</c>
/// serves (ADR-116): list the host's roles, read one, read the catalog an editor draws itself from,
/// and replace the STORED permissions of one role.
/// </summary>
/// <remarks>
/// <para>
/// Non-generic, unlike <see cref="IUserAdminUIService{TUserDto}"/>: a role is a string and a
/// permission is a string, so there is no app-owned DTO for an app to supply.
/// </para>
/// <para>
/// Every member answers with a <see cref="Result"/> carrying the API's own errors and their
/// <see cref="ErrorType"/> intact, so a page branches on the outcome instead of catching. Registered
/// by <c>AddRoleAdministrationUI</c>.
/// </para>
/// </remarks>
public interface IRoleAdminUIService
{
    /// <summary>Reads every role with its compiled and stored permissions, from <c>GET Admin/Roles</c>.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The roles, or the API's own refusal.</returns>
    Task<Result<IReadOnlyList<RolePermissionsResponse>>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one role, from <c>GET Admin/Roles/{role}</c>.</summary>
    /// <param name="role">The role name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role, or the API's own refusal.</returns>
    Task<Result<RolePermissionsResponse>> GetAsync(string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the closed sets an editor renders (roles, and the permissions a stored grant may name),
    /// from <c>GET Admin/Roles/catalog</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catalog, or the API's own refusal.</returns>
    Task<Result<PermissionCatalogResponse>> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored permissions of one role, through
    /// <c>PUT Admin/Roles/{role}/permissions</c>.
    /// </summary>
    /// <param name="role">The role to edit.</param>
    /// <param name="permissions">The complete set of stored permissions the role should grant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role's state after the edit, or the API's own refusal.</returns>
    Task<Result<RolePermissionsResponse>> SetStoredPermissionsAsync(
        string role,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default);
}
