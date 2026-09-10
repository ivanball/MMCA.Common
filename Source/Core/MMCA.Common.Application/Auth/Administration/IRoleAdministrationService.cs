using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Administration;

/// <summary>
/// The surface <c>RolesAdminControllerBase</c> talks to: list the host's roles with their permissions,
/// and replace the STORED permissions of one role.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="IUserAdministrationService{TUserDto}"/>, this one has a shipped implementation:
/// everything it needs is already framework-owned (the compiled <c>IPermissionRegistry</c> and the
/// stored <c>IPermissionGrantStore</c>), so <c>AddStoredPermissionGrants()</c> registers a default
/// with <c>TryAdd</c> and an app only implements it to change the behavior.
/// </para>
/// <para>
/// Setting permissions replaces the stored set for that role and invalidates the cached snapshot, so
/// the edit is live on the next request in this process. Compiled permissions are reported but are
/// never touched by a set.
/// </para>
/// </remarks>
public interface IRoleAdministrationService
{
    /// <summary>
    /// Lists the host's roles, each with the permissions its registry compiles in and the permissions
    /// stored rows grant it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per known role, ordered by role name.</returns>
    Task<Result<IReadOnlyList<RolePermissionsResponse>>> ListRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one role's compiled and stored permissions.
    /// </summary>
    /// <param name="role">The role name, matched case-insensitively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role, or a not-found failure when the host knows no such role.</returns>
    Task<Result<RolePermissionsResponse>> GetRoleAsync(string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored permissions of one role and invalidates the cached snapshot.
    /// </summary>
    /// <param name="role">The role to edit.</param>
    /// <param name="permissions">The complete set of stored permissions the role should grant.</param>
    /// <param name="changedBy">Optional principal name recorded on the rows this creates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role's state after the edit, or a failure.</returns>
    Task<Result<RolePermissionsResponse>> SetStoredPermissionsAsync(
        string role,
        IReadOnlyList<string> permissions,
        string? changedBy = null,
        CancellationToken cancellationToken = default);
}
