using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Controllers.Administration;

/// <summary>
/// Base controller for the opt-in role-administration endpoints: list roles with their permissions,
/// read one, and replace the STORED permission grants of one role.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and the API version, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Admin/Roles")]
/// [ApiVersion("1.0")]
/// public sealed class AdminRolesController(
///     IRoleAdministrationService service, ICurrentUserService currentUser)
///     : RolesAdminControllerBase(service, currentUser);
/// </code>
/// <para>
/// Unlike the user surface, this one needs no app-supplied service:
/// <c>AddStoredPermissionGrants()</c> registers a complete
/// <see cref="IRoleAdministrationService"/> over the framework's own registry and grant store, and an
/// app implements the interface only to change that behavior.
/// </para>
/// <para>
/// <b>A set edits data, never code.</b> The compiled permissions the response reports are read-only;
/// a set replaces the stored grants alone, so no request through this controller can take away a
/// capability the host compiled into its registry.
/// </para>
/// <para>
/// <b>Two refusals a client has to expect from a set</b>, both 400 Bad Request:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>PermissionGrant.ManageRolesMustBeCompiled</c> when the submitted set names
/// <see cref="AdministrationPermissions.ManageRoles"/>. That permission is the whole protection on
/// this controller, so granting it from here would make access to role administration a matter of
/// data, and deleting the row would lock every operator out of the screen that could restore it. A
/// host that wants a role to administer roles grants it in code.
/// </item>
/// <item>
/// <c>PermissionGrant.UnknownPermission</c> when the set names anything outside the catalog
/// <see cref="GetCatalogAsync"/> reports. A stored row that no endpoint checks is a typo, not a
/// grant, so it is refused rather than written and left silently inert.
/// </item>
/// </list>
/// </remarks>
/// <param name="administration">The role-administration service.</param>
/// <param name="currentUserService">The caller, recorded on the grant rows a set creates.</param>
[HasPermission(AdministrationPermissions.ManageRoles)]
public abstract class RolesAdminControllerBase(
    IRoleAdministrationService administration,
    ICurrentUserService currentUserService) : ApiControllerBase
{
    /// <summary>The role-administration service (exposed for app-level overrides).</summary>
    protected IRoleAdministrationService Administration { get; } = administration;

    /// <summary>The current user service (exposed for app-level overrides).</summary>
    protected ICurrentUserService CurrentUserService { get; } = currentUserService;

    /// <summary>
    /// Lists the host's roles, each with its compiled and its stored permissions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The roles, or a Problem Details failure.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RolePermissionsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<IReadOnlyList<RolePermissionsResponse>>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.ListRolesAsync(cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Reads the closed sets a role editor renders: the roles this surface lists, and the
    /// permissions a stored grant may name.
    /// </summary>
    /// <remarks>
    /// Routed on the literal <c>catalog</c>, which takes precedence over the <c>{role}</c> template
    /// below, so no role named "catalog" can shadow it.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catalog, or a Problem Details failure.</returns>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(PermissionCatalogResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<PermissionCatalogResponse>> GetCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.GetCatalogAsync(cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Reads one role's compiled and stored permissions.
    /// </summary>
    /// <param name="role">The role name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role, or a Problem Details failure.</returns>
    [HttpGet("{role}")]
    [ProducesResponseType(typeof(RolePermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<RolePermissionsResponse>> GetAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.GetRoleAsync(role, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Replaces the stored permission grants of one role and returns its state afterwards.
    /// </summary>
    /// <param name="role">The role to edit.</param>
    /// <param name="request">The complete set of stored permissions the role should grant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role's state after the edit, or a Problem Details failure.</returns>
    [HttpPut("{role}/permissions")]
    [ProducesResponseType(typeof(RolePermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<RolePermissionsResponse>> SetPermissionsAsync(
        string role,
        [FromBody] SetRolePermissionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await Administration
            .SetStoredPermissionsAsync(role, request.Permissions, ResolveChangedBy(), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// The principal name recorded on the grant rows a set creates. Defaults to the caller's user
    /// identifier, which is what a token always carries; override to record a display name instead.
    /// </summary>
    /// <returns>The audit label, or <see langword="null"/> when the caller cannot be identified.</returns>
    protected virtual string? ResolveChangedBy() =>
        CurrentUserService.UserId?.ToString(CultureInfo.InvariantCulture);
}
