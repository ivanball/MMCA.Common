namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Accumulates role-to-permission grants and builds an immutable <see cref="PermissionRegistry"/>.
/// Multiple modules can contribute grants for the same role; grants are unioned, so each module
/// can declare only the permissions it owns without knowing about the others.
/// </summary>
public sealed class PermissionRegistryBuilder
{
    private readonly Dictionary<string, HashSet<string>> _grants =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Grants one or more permissions to a role. Additive and idempotent — duplicate grants
    /// (across calls or modules) are merged.
    /// </summary>
    /// <param name="role">The role receiving the permissions.</param>
    /// <param name="permissions">The permissions to grant.</param>
    /// <returns>The same builder, for chaining.</returns>
    public PermissionRegistryBuilder Grant(string role, params string[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(permissions);

        if (!_grants.TryGetValue(role, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _grants[role] = set;
        }

        foreach (var permission in permissions.Where(permission => !string.IsNullOrWhiteSpace(permission)))
        {
            set.Add(permission);
        }

        return this;
    }

    /// <summary>Builds an immutable registry snapshot of the accumulated grants.</summary>
    /// <returns>A new <see cref="PermissionRegistry"/>.</returns>
    public PermissionRegistry Build()
    {
        var map = _grants.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);

        return new PermissionRegistry(map);
    }
}
