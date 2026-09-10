using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Application.Auth.Permissions;

/// <summary>
/// An <see cref="IPermissionRegistry"/> that answers from the host's compiled registry FIRST and then
/// from the stored grants, so an operator can widen a role without a deploy while the capabilities the
/// code depends on stay compiled in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Union, not override.</b> The two layers are added together. There is no stored denial, so a
/// stored row can only ever grant more than the code does; removing a compiled-in permission is a code
/// change. That keeps the effective permission set independent of evaluation order and stops a data
/// edit from silently disabling an endpoint.
/// </para>
/// <para>
/// <b>The contract is unchanged.</b> This is a decorator registered over whatever
/// <see cref="IPermissionRegistry"/> the host already had, so the authorization decorators, the
/// <c>[HasPermission]</c> policy handler and every other reader keep asking the same two synchronous
/// questions and never learn that a second layer exists.
/// </para>
/// <para>
/// The compiled layer is asked first because it is a frozen set lookup and the common answer; the
/// cache is only consulted when it says no.
/// </para>
/// </remarks>
/// <param name="inner">The host's compiled registry, consulted first.</param>
/// <param name="grants">The stored grants, consulted second.</param>
public sealed class LayeredPermissionRegistry(
    IPermissionRegistry inner,
    IPermissionGrantCache grants) : IPermissionRegistry
{
    /// <inheritdoc />
    public IReadOnlySet<string> GetPermissions(string role)
    {
        var compiled = inner.GetPermissions(role);
        var stored = grants.GetPermissions(role);

        if (stored.Count == 0)
        {
            return compiled;
        }

        // Only allocate when both layers actually contribute. GetPermissions is a reporting call (the
        // administration surface, a diagnostics page), not the hot authorization path, which is
        // HasPermission below.
        var union = new HashSet<string>(compiled, StringComparer.Ordinal);
        union.UnionWith(stored);

        return union;
    }

    /// <inheritdoc />
    public bool HasPermission(IEnumerable<string> roles, string permission)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        // Materialized once: the compiled pass and the stored pass both enumerate, and the caller's
        // sequence is a claim projection that would otherwise be walked twice.
        var roleList = roles as IReadOnlyList<string> ?? [.. roles];

        return inner.HasPermission(roleList, permission)
            || roleList.Any(role => role is not null && grants.GetPermissions(role).Contains(permission));
    }
}
