using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.API.Controllers.Administration;

/// <summary>
/// Base controller for the opt-in user-administration endpoints: list accounts (paged), read one,
/// lock and unlock, and replace an account's roles.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route, the API version and the app's DTO, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Admin/Users")]
/// [ApiVersion("1.0")]
/// public sealed class AdminUsersController(IUserAdministrationService&lt;UserDTO&gt; service)
///     : UsersAdminControllerBase&lt;UserDTO&gt;(service);
/// </code>
/// <para>
/// The route lives on the subclass (the <c>AuthControllerBase</c> precedent) because the app owns its
/// URL space; the action templates below are fixed, so a subclass routed at <c>Admin/Users</c> serves
/// <c>/Admin/Users/paged</c>, <c>/Admin/Users/{userId}</c> and the rest.
/// </para>
/// <para>
/// <b>Gated on a capability, not a role name.</b> Every action requires
/// <see cref="AdministrationPermissions.ManageUsers"/>, resolved through the host's
/// <c>IPermissionRegistry</c>, so an app decides which of its roles is an administrator without this
/// file knowing any of them. A host that mounts the controller and grants the permission to nobody
/// serves endpoints that deny everyone, which is the safe direction.
/// </para>
/// <para>
/// Paging matches the framework's read endpoints: <c>pageNumber</c>/<c>pageSize</c> query parameters,
/// a page size clamped to <see cref="MaxPageSize"/>, and the pagination metadata echoed in the
/// <c>X-Pagination</c> response header as well as in the body.
/// </para>
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
/// <param name="administration">The app's user-administration service.</param>
[HasPermission(AdministrationPermissions.ManageUsers)]
public abstract class UsersAdminControllerBase<TUserDto>(
    IUserAdministrationService<TUserDto> administration) : ApiControllerBase
{
    /// <summary>The largest page this endpoint will serve, whatever the caller asks for.</summary>
    public const int MaxPageSize = 100;

    /// <summary>The app's user-administration service (exposed for app-level overrides).</summary>
    protected IUserAdministrationService<TUserDto> Administration { get; } = administration;

    /// <summary>
    /// Returns one page of accounts, optionally filtered by a free-text term and by role.
    /// </summary>
    /// <param name="searchTerm">Optional free-text filter; the service decides which fields it covers.</param>
    /// <param name="role">Optional role filter.</param>
    /// <param name="pageNumber">The 1-based page to return.</param>
    /// <param name="pageSize">Accounts per page, clamped to <see cref="MaxPageSize"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or a Problem Details failure.</returns>
    [HttpGet("paged")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<PagedCollectionResult<TUserDto>>> GetPagedAsync(
        [FromQuery] string? searchTerm = null,
        [FromQuery] string? role = null,
        [FromQuery][Range(1, int.MaxValue)] int pageNumber = 1,
        [FromQuery][Range(1, int.MaxValue)] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var query = new UserAdministrationQuery(
            pageNumber,
            Math.Min(pageSize, MaxPageSize),
            searchTerm,
            role);

        var result = await Administration.ListAsync(query, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return HandleFailure(result.Errors);
        }

        Response.Headers.Append(
            "X-Pagination",
            JsonSerializer.Serialize(result.Value!.PaginationMetadata, JsonSerializerOptions.Web));

        return Ok(result.Value);
    }

    /// <summary>
    /// Returns one account.
    /// </summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account, or a Problem Details failure.</returns>
    [HttpGet("{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TUserDto>> GetAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.GetAsync(userId, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Locks an account, so it can no longer sign in.
    /// </summary>
    /// <remarks>
    /// Locking does not by itself invalidate an access token the account already holds; a service
    /// implementation should revoke the account's live refresh sessions too, which is what makes the
    /// lock take effect within one access-token lifetime rather than at the next sign-in.
    /// </remarks>
    /// <param name="userId">The account to lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpPost("{userId}/lock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> LockAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.SetLockedAsync(userId, locked: true, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }

    /// <summary>
    /// Unlocks an account.
    /// </summary>
    /// <param name="userId">The account to unlock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpPost("{userId}/unlock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> UnlockAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.SetLockedAsync(userId, locked: false, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }

    /// <summary>
    /// Replaces the set of roles an account holds.
    /// </summary>
    /// <param name="userId">The account to re-role.</param>
    /// <param name="request">The complete set of roles the account should hold afterwards.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpPut("{userId}/roles")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> SetRolesAsync(
        UserIdentifierType userId,
        [FromBody] SetUserRolesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await Administration
            .SetRolesAsync(userId, request.Roles, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }
}
