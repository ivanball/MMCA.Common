namespace MMCA.Common.Shared.Auth.Permissions;

/// <summary>
/// The permission names the framework's opt-in administration controller bases are gated on. They
/// follow the <c>area:capability</c> shape <see cref="IPermissionRegistry"/> documents, so a host
/// grants them the same way it grants its own: through
/// <see cref="PermissionRegistryBuilder.Grant"/>, or through a stored grant row.
/// </summary>
/// <remarks>
/// Naming them here rather than in each app is what lets the shared controller bases carry their own
/// <c>[HasPermission]</c> attribute. A host that never calls those bases never has to know these
/// values exist, and a host that mounts them but grants neither permission serves endpoints that
/// deny everyone, which is the safe direction.
/// </remarks>
public static class AdministrationPermissions
{
    /// <summary>Lists, inspects, locks, unlocks and re-roles user accounts.</summary>
    public const string ManageUsers = "users:manage";

    /// <summary>Lists roles and edits the stored permission grants attached to them.</summary>
    public const string ManageRoles = "roles:manage";
}
