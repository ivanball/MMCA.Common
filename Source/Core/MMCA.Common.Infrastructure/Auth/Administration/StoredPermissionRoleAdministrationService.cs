using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Infrastructure.Auth.Administration;

/// <summary>
/// The shipped <see cref="IRoleAdministrationService"/>: reports each role's compiled and stored
/// permissions, and replaces the stored half.
/// </summary>
/// <remarks>
/// <para>
/// It can ship complete because every half is framework-owned: <see cref="IPermissionRegistry"/>
/// answers what the code grants, <see cref="IPermissionCatalog"/> enumerates what the code CAN
/// grant, and <see cref="IPermissionGrantStore"/> holds what data grants. The role universe is the
/// catalog's roles, unioned with <c>Authentication:PermissionGrants:KnownRoles</c> and with every
/// role that already carries a stored grant, so a role reachable by any of the three routes is
/// listed exactly once.
/// </para>
/// <para>
/// <b>Two refusals on a set.</b> The catalog is a closed list, so a permission outside it is a typo
/// rather than an intent (<c>PermissionGrant.UnknownPermission</c>). And
/// <see cref="AdministrationPermissions.ManageRoles"/> is refused outright
/// (<c>PermissionGrant.ManageRolesMustBeCompiled</c>): granting the key to this surface from inside
/// this surface makes the permission that guards it a matter of data, and a deleted row would then
/// lock every operator out of the screen that could restore it. A host that wants a role to
/// administer roles compiles that grant in.
/// </para>
/// <para>
/// A set is a diff, not a truncate-and-insert: only the permissions that actually changed are written,
/// so an unchanged submission writes nothing and the <c>GrantedAt</c> stamps of untouched rows survive.
/// The snapshot is invalidated once, after the writes, so the edit is live on the next request in this
/// process.
/// </para>
/// </remarks>
/// <param name="registry">The effective registry, whose answer includes the stored layer.</param>
/// <param name="catalog">The compiled universe: the roles listed and the permissions a set may name.</param>
/// <param name="store">The stored grants, which is what a set edits.</param>
/// <param name="cache">The cached stored grants, subtracted to report the compiled half alone.</param>
/// <param name="invalidator">Reloads the cached snapshot after an edit.</param>
/// <param name="settings">Bound settings, supplying the configured role list.</param>
internal sealed class StoredPermissionRoleAdministrationService(
    IPermissionRegistry registry,
    IPermissionCatalog catalog,
    IPermissionGrantStore store,
    IPermissionGrantCache cache,
    IPermissionGrantCacheInvalidator invalidator,
    IOptions<PermissionGrantSettings> settings) : IRoleAdministrationService
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RolePermissionsResponse>>> ListRolesAsync(
        CancellationToken cancellationToken = default)
    {
        var grants = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var storedByRole = grants
            .GroupBy(grant => grant.Role, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)[.. group.Select(grant => grant.Permission).Order(StringComparer.Ordinal)],
                StringComparer.OrdinalIgnoreCase);

        var roles = RoleUniverse(storedByRole.Keys);

        IReadOnlyList<RolePermissionsResponse> result =
        [
            .. roles.Select(role => new RolePermissionsResponse(
                role,
                CompiledPermissions(role),
                storedByRole.TryGetValue(role, out var stored) ? stored : []))
        ];

        return Result.Success(result);
    }

    /// <inheritdoc />
    public async Task<Result<PermissionCatalogResponse>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var grants = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var roles = RoleUniverse(grants.Select(grant => grant.Role));

        // The permission half is the compiled catalog alone: it is the closed list a set may name,
        // and widening it with whatever happens to be stored would let one typo legitimize itself.
        return Result.Success(new PermissionCatalogResponse([.. roles], catalog.Permissions));
    }

    /// <inheritdoc />
    public async Task<Result<RolePermissionsResponse>> GetRoleAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return Result.Failure<RolePermissionsResponse>(RoleNotFound(role));
        }

        var stored = await store.GetPermissionsAsync(role, cancellationToken).ConfigureAwait(false);
        var compiled = CompiledPermissions(role);

        // A role the host has never named, never compiled a permission for and never granted one to
        // does not exist as far as this surface is concerned. Reporting it as an empty role instead
        // would make every typo look like a real role with nothing granted.
        if (stored.Count == 0
            && compiled.Count == 0
            && !settings.Value.KnownRoles.Contains(role, StringComparer.OrdinalIgnoreCase)
            && !catalog.Roles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return Result.Failure<RolePermissionsResponse>(RoleNotFound(role));
        }

        return Result.Success(new RolePermissionsResponse(role, compiled, stored));
    }

    /// <inheritdoc />
    public async Task<Result<RolePermissionsResponse>> SetStoredPermissionsAsync(
        string role,
        IReadOnlyList<string> permissions,
        string? changedBy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (string.IsNullOrWhiteSpace(role))
        {
            return Result.Failure<RolePermissionsResponse>(RoleNotFound(role));
        }

        var desired = new HashSet<string>(
            permissions.Where(permission => !string.IsNullOrWhiteSpace(permission)).Select(permission => permission.Trim()),
            StringComparer.Ordinal);

        // Checked before the catalog test, and regardless of whether the host compiled this
        // permission in: the message has to name the real reason rather than "unknown".
        if (desired.Contains(AdministrationPermissions.ManageRoles))
        {
            return Result.Failure<RolePermissionsResponse>(Error.Validation(
                "PermissionGrant.ManageRolesMustBeCompiled",
                $"\"{AdministrationPermissions.ManageRoles}\" cannot be granted by a stored row. It is the permission that guards this surface, so granting it from here would make access to role administration a matter of data, and deleting the row would lock every operator out of the screen that could restore it. Grant it in code, through the host's permission registry.",
                nameof(IRoleAdministrationService),
                role));
        }

        var unknown = desired
            .Except(catalog.Permissions, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
        {
            return Result.Failure<RolePermissionsResponse>(Error.Validation(
                "PermissionGrant.UnknownPermission",
                $"The host's permission catalog does not contain {string.Join(", ", unknown.Select(permission => $"\"{permission}\""))}. A stored grant may only name a permission the code already declares, so an endpoint actually checks it.",
                nameof(IRoleAdministrationService),
                role));
        }

        var current = await store.GetPermissionsAsync(role, cancellationToken).ConfigureAwait(false);
        var existing = new HashSet<string>(current, StringComparer.Ordinal);

        foreach (var permission in desired.Except(existing, StringComparer.Ordinal))
        {
            var granted = await store
                .GrantAsync(role, permission, changedBy, cancellationToken)
                .ConfigureAwait(false);

            if (granted.IsFailure)
            {
                return Result.Failure<RolePermissionsResponse>(granted.Errors);
            }
        }

        foreach (var permission in existing.Except(desired, StringComparer.Ordinal))
        {
            var revoked = await store.RevokeAsync(role, permission, cancellationToken).ConfigureAwait(false);
            if (revoked.IsFailure)
            {
                return Result.Failure<RolePermissionsResponse>(revoked.Errors);
            }
        }

        await invalidator.InvalidateAsync(role, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> stored = [.. desired.Order(StringComparer.Ordinal)];

        return Result.Success(new RolePermissionsResponse(role, CompiledPermissions(role), stored));
    }

    /// <summary>
    /// Every role this surface knows: the compiled catalog's, the configured
    /// <c>KnownRoles</c>, and the roles that already carry a stored grant, de-duplicated
    /// case-insensitively and ordered.
    /// </summary>
    /// <param name="rolesWithGrants">The roles named by stored rows.</param>
    /// <returns>The role universe, sorted.</returns>
    private SortedSet<string> RoleUniverse(IEnumerable<string> rolesWithGrants)
    {
        var roles = new SortedSet<string>(catalog.Roles, StringComparer.OrdinalIgnoreCase);
        roles.UnionWith(settings.Value.KnownRoles);
        roles.UnionWith(rolesWithGrants);

        return roles;
    }

    private static Error RoleNotFound(string? role) => Error.NotFoundError(
        "Authorization.RoleNotFound",
        "The role was not found.",
        nameof(IRoleAdministrationService),
        role ?? string.Empty);

    /// <summary>
    /// The permissions the compiled registry grants a role.
    /// </summary>
    /// <remarks>
    /// Read through the SAME registry the authorization path uses, which is the layered one once
    /// stored grants are wired. The stored half is subtracted so the two lists this surface reports
    /// stay disjoint, and the read-only list really is only what the code grants.
    /// </remarks>
    /// <param name="role">The role name.</param>
    /// <returns>The compiled permissions, ordered.</returns>
    private IReadOnlyList<string> CompiledPermissions(string role) =>
        [.. registry.GetPermissions(role)
            .Except(cache.GetPermissions(role), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
