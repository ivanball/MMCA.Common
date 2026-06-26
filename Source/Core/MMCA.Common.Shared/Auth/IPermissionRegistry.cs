namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Maps roles to the permissions they grant. Endpoints are authorized against fine-grained
/// permissions (capabilities), and this registry is the single place that knows which roles
/// confer which permissions. That keeps endpoints decoupled from role names: adding a new role
/// or re-shaping who-can-do-what is a registry change, not an endpoint change.
/// </summary>
/// <remarks>
/// Role lookups are case-insensitive (see <see cref="RoleNames"/>); permission values are
/// compared ordinally. Implementations are expected to be immutable and thread-safe.
/// </remarks>
public interface IPermissionRegistry
{
    /// <summary>
    /// Gets the permissions granted to the specified role, or an empty set if the role is unknown.
    /// </summary>
    /// <param name="role">The role name.</param>
    /// <returns>The permissions granted to <paramref name="role"/>.</returns>
    IReadOnlySet<string> GetPermissions(string role);

    /// <summary>
    /// Returns <see langword="true"/> if any of the supplied roles grants the permission.
    /// </summary>
    /// <param name="roles">The principal's roles.</param>
    /// <param name="permission">The required permission.</param>
    /// <returns><see langword="true"/> if at least one role grants the permission.</returns>
    bool HasPermission(IEnumerable<string> roles, string permission);
}
