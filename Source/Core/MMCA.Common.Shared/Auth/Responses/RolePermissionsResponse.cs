namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// One role as the administration API reports it: its name, the permissions compiled into the host's
/// registry, and the permissions granted to it by stored rows.
/// </summary>
/// <remarks>
/// The two sets are reported apart rather than merged because only one of them is editable. Merging
/// them would let an operator try to revoke a compiled-in capability and see the removal silently do
/// nothing, since the static registry keeps granting it.
/// </remarks>
/// <param name="Role">The role name.</param>
/// <param name="RegisteredPermissions">
/// Permissions the host's compiled <c>IPermissionRegistry</c> grants this role. Read-only.
/// </param>
/// <param name="StoredPermissions">
/// Permissions granted by stored rows, which is what a set operation replaces.
/// </param>
public sealed record RolePermissionsResponse(
    string Role,
    IReadOnlyList<string> RegisteredPermissions,
    IReadOnlyList<string> StoredPermissions);
