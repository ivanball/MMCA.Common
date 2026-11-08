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

    /// <summary>
    /// Views the app read-only as another account for at most 15 minutes (ADR-137). An account that
    /// holds it can never be viewed itself, so staff cannot view each other.
    /// </summary>
    public const string ViewAsUsers = "users:view-as";

    /// <summary>
    /// Lists the declared feature flags and sets or clears their stored overrides (ADR-031), so a kill
    /// switch flips without a new revision.
    /// </summary>
    public const string ManageFeatures = "features:manage";

    /// <summary>
    /// Lists machine clients and creates, rotates, promotes and revokes them (ADR-140). Creating a
    /// client creates the service-account user behind it, so grant it as sparingly as
    /// <see cref="ManageUsers"/>.
    /// </summary>
    public const string ManageClients = "clients:manage";
}
