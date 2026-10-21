using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Administration;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Canned <see cref="IRoleAdminUIService"/> for the gallery so the shared <c>RoleAdminList</c> and
/// <c>RoleAdminEdit</c> components render populated, real markup for the axe/render E2E scan, with
/// no backend: two roles, a catalog spanning two areas, and one compiled plus one stored grant so
/// both kinds of locked and editable checkbox appear.
/// </summary>
internal sealed class StubRoleAdminUIService : IRoleAdminUIService
{
    private static readonly RolePermissionsResponse[] Roles =
    [
        new("Admin", ["orders:read", "orders:write"], ["reports:run"]),
        new("Member", ["orders:read"], []),
    ];

    public Task<Result<IReadOnlyList<RolePermissionsResponse>>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<RolePermissionsResponse>>(Roles));

    public Task<Result<RolePermissionsResponse>> GetAsync(string role, CancellationToken cancellationToken = default)
    {
        var match = Array.Find(Roles, r => string.Equals(r.Role, role, StringComparison.Ordinal));
        return Task.FromResult(match is null
            ? Result.Failure<RolePermissionsResponse>(Error.NotFoundError("Authorization.RoleNotFound", "The role was not found."))
            : Result.Success(match));
    }

    public Task<Result<PermissionCatalogResponse>> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new PermissionCatalogResponse(
            [.. Roles.Select(r => r.Role)],
            ["orders:read", "orders:write", "reports:run", AdministrationPermissions.ManageRoles])));

    public Task<Result<RolePermissionsResponse>> SetStoredPermissionsAsync(
        string role,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default)
    {
        var compiled = Array.Find(Roles, r => string.Equals(r.Role, role, StringComparison.Ordinal))?.RegisteredPermissions ?? [];
        return Task.FromResult(Result.Success(new RolePermissionsResponse(role, compiled, permissions)));
    }
}
