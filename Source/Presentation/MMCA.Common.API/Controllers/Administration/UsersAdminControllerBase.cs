using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Controllers.Administration;

/// <summary>
/// Base controller for the opt-in user-administration endpoints: read one account, list its live
/// sessions and its sign-in history, lock and unlock, and replace its roles. The roster itself is the app's generic paged
/// entity endpoint, not an action here.
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
/// <c>/Admin/Users/{userId}</c> and the rest.
/// </para>
/// <para>
/// <b>Gated on a capability, not a role name.</b> Every action requires
/// <see cref="AdministrationPermissions.ManageUsers"/>, resolved through the host's
/// <c>IPermissionRegistry</c>, so an app decides which of its roles is an administrator without this
/// file knowing any of them. A host that mounts the controller and grants the permission to nobody
/// serves endpoints that deny everyone, which is the safe direction.
/// </para>
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
/// <param name="administration">The app's user-administration service.</param>
[HasPermission(AdministrationPermissions.ManageUsers)]
public abstract class UsersAdminControllerBase<TUserDto>(
    IUserAdministrationService<TUserDto> administration) : ApiControllerBase
{
    /// <summary>The app's user-administration service (exposed for app-level overrides).</summary>
    protected IUserAdministrationService<TUserDto> Administration { get; } = administration;

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
    /// Returns the account's live (signed-in) sessions, newest first. View only.
    /// </summary>
    /// <param name="userId">The account whose sessions to read.</param>
    /// <param name="sessions">The framework's session administration service, resolved per action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The live sessions, or a Problem Details failure.</returns>
    [HttpGet("{userId}/sessions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        [FromServices] IUserSessionsAdministrationService sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var result = await sessions.GetSessionsAsync(userId, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Returns the account's sign-in history: one row per sign-in, ended ones included with how they
    /// ended, newest first, capped at the newest 50. View only.
    /// </summary>
    /// <param name="userId">The account whose sign-ins to read.</param>
    /// <param name="sessions">The framework's session administration service, resolved per action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The sign-ins, or a Problem Details failure.</returns>
    [HttpGet("{userId}/sign-ins")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<IReadOnlyList<SignInSummaryResponse>>> GetSignInsAsync(
        UserIdentifierType userId,
        [FromServices] IUserSessionsAdministrationService sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var result = await sessions.GetSignInsAsync(userId, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Starts a read-only, 15-minute view of the app as this account (ADR-137) and returns its token.
    /// Requires <see cref="AdministrationPermissions.ViewAsUsers"/> on top of the class's
    /// <see cref="AdministrationPermissions.ManageUsers"/>, and a reason. Refused for the caller's own
    /// account, a deleted account, and an account that itself holds <c>users:view-as</c>.
    /// </summary>
    /// <remarks>
    /// 404 while <c>ViewAs:Enabled</c> is false, so a host that has not opted in serves nothing here,
    /// and on a service that does not hold the grant store (<c>ViewAs:HostsGrantStore</c> false).
    /// On a web host with the same-origin API proxy the proxy moves the token into its overlay cookie;
    /// the admin's own session is untouched.
    /// </remarks>
    /// <param name="userId">The account to view.</param>
    /// <param name="request">The reason for the access.</param>
    /// <param name="settings">The view-as settings, resolved per action.</param>
    /// <param name="viewAs">The view-as service, resolved per action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The view-as token, or a Problem Details failure.</returns>
    [HttpPost("{userId}/view-as")]
    [HasPermission(AdministrationPermissions.ViewAsUsers)]
    [NonIdempotent("Each call issues a new grant row, which is the audit record of the access. A replayed response would hand back a token for a grant the caller may already have ended.")]
    [ProducesResponseType(typeof(ViewAsGrantResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<ViewAsGrantResponse>> StartViewAsAsync(
        UserIdentifierType userId,
        [FromBody] StartViewAsRequest request,
        [FromServices] IOptions<ViewAsSettings> settings,
        [FromServices] IViewAsService viewAs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(viewAs);

        if (!settings.Value.Enabled || !settings.Value.HostsGrantStore)
        {
            return NotFound();
        }

        if (User.GetUserId() is not { } staffUserId)
        {
            return Unauthorized();
        }

        var result = await viewAs
            .StartAsync(staffUserId, userId, request?.Reason, cancellationToken)
            .ConfigureAwait(false);

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
