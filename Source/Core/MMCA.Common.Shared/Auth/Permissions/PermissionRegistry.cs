using System.Collections.Frozen;

namespace MMCA.Common.Shared.Auth.Permissions;

/// <summary>
/// Immutable <see cref="IPermissionRegistry"/> built from a role-to-permissions map.
/// Role lookups are case-insensitive; permission values are compared ordinally.
/// Construct one via <see cref="PermissionRegistryBuilder"/>.
/// <para>
/// It is also the host's <see cref="IPermissionCatalog"/>: the same map that answers "does this role
/// grant that permission" already knows the closed set of roles and permissions the code compiled
/// in, so the enumeration an administration screen needs is a projection of it rather than a second
/// source that could drift.
/// </para>
/// </summary>
public sealed class PermissionRegistry : IPermissionRegistry, IPermissionCatalog
{
    private static readonly FrozenSet<string> EmptyPermissions = [];

    private readonly FrozenDictionary<string, FrozenSet<string>> _rolePermissions;
    private readonly IReadOnlyList<string> _roles;
    private readonly IReadOnlyList<string> _permissions;

    /// <summary>
    /// Initializes the registry from a role-to-permissions map. Prefer
    /// <see cref="PermissionRegistryBuilder"/> over calling this directly.
    /// </summary>
    /// <param name="rolePermissions">The role-to-permissions map.</param>
    public PermissionRegistry(IReadOnlyDictionary<string, IReadOnlySet<string>> rolePermissions)
    {
        ArgumentNullException.ThrowIfNull(rolePermissions);

        _rolePermissions = rolePermissions.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);

        // Materialized once at construction: the registry is immutable, and the catalog is read by a
        // rendering surface that would otherwise re-sort the whole map on every request.
        _roles = [.. _rolePermissions.Keys.Order(StringComparer.Ordinal)];
        _permissions =
        [
            .. _rolePermissions.Values
                .SelectMany(permissions => permissions)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
    }

    // Explicit, because the catalog's "Permissions" and the registry's "GetPermissions(role)" answer
    // different questions and a reader holding the concrete type should not have to tell them apart
    // (CA1721 says the same thing). The catalog is consumed through IPermissionCatalog.

    /// <inheritdoc />
    IReadOnlyList<string> IPermissionCatalog.Roles => _roles;

    /// <inheritdoc />
    IReadOnlyList<string> IPermissionCatalog.Permissions => _permissions;

    /// <inheritdoc />
    public IReadOnlySet<string> GetPermissions(string role) =>
        role is not null && _rolePermissions.TryGetValue(role, out var permissions)
            ? permissions
            : EmptyPermissions;

    /// <inheritdoc />
    public bool HasPermission(IEnumerable<string> roles, string permission)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        foreach (var role in roles)
        {
            if (role is not null
                && _rolePermissions.TryGetValue(role, out var permissions)
                && permissions.Contains(permission))
            {
                return true;
            }
        }

        return false;
    }
}
