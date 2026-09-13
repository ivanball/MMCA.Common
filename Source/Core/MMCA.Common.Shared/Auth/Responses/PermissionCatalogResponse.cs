namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// Everything a role editor needs to draw itself: the roles the host knows and the closed set of
/// permissions a stored grant may pick from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Permissions"/> is the COMPILED universe, taken from the host's
/// <c>IPermissionCatalog</c>. A stored grant can only ever pick out of it, so an editor that renders
/// this list can never submit a permission the server will reject as unknown.
/// </para>
/// <para>
/// <see cref="Roles"/> is wider than the catalog's own: it also carries the roles configured under
/// <c>Authentication:PermissionGrants:KnownRoles</c> and the roles that already carry a stored
/// grant, because a role with no compiled permission is still a role an operator has to be able to
/// widen.
/// </para>
/// </remarks>
/// <param name="Roles">Every role the administration surface lists, sorted ordinally.</param>
/// <param name="Permissions">Every permission a stored grant may name, sorted ordinally.</param>
public sealed record PermissionCatalogResponse(
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
