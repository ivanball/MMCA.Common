namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Accumulates role-to-permission grants and builds an immutable <see cref="PermissionRegistry"/>.
/// Multiple modules can contribute grants for the same role; grants are unioned, so each module
/// can declare only the permissions it owns without knowing about the others.
/// </summary>
public sealed class PermissionRegistryBuilder
{
    // IDE0028 suggests a collection expression here, but it cannot carry the OrdinalIgnoreCase
    // comparer that keeps role keys case-insensitive (matching RoleValue). The concrete Dictionary
    // type is kept for CA1859 (perf).
#pragma warning disable IDE0028 // Collection initialization can be simplified
    private readonly Dictionary<string, HashSet<string>> _grants =
        new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0028

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

        var granted = permissions.Where(permission => !string.IsNullOrWhiteSpace(permission));

        if (_grants.TryGetValue(role, out var set))
        {
            set.UnionWith(granted);
        }
        else
        {
            _grants[role] = granted.ToHashSet(StringComparer.Ordinal);
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
