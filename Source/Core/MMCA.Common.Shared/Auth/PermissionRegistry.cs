using System.Collections.Frozen;

namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Immutable <see cref="IPermissionRegistry"/> built from a role-to-permissions map.
/// Role lookups are case-insensitive; permission values are compared ordinally.
/// Construct one via <see cref="PermissionRegistryBuilder"/>.
/// </summary>
public sealed class PermissionRegistry : IPermissionRegistry
{
    private static readonly FrozenSet<string> EmptyPermissions = [];

    private readonly FrozenDictionary<string, FrozenSet<string>> _rolePermissions;

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
    }

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
