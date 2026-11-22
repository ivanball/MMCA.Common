using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Authorization.MultiFactor;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Shared.Abstractions;
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
/// <para>
/// <b>No administrator grants more than they hold.</b> When the host registers an
/// <see cref="IPermissionRegistry"/>, the role, lock and unlock actions are refused with 403 and the
/// stable code <c>UserAdministration.PrivilegeCeiling</c> when the requested role, or the target
/// account's current role (<see cref="IUserAdministrationService{TUserDto}.GetRoleAsync"/>), grants a
/// permission the caller does not hold, evaluated as the permission authorization handler evaluates
/// it. The refusal names no permission. A host with no registry (a role-only host) applies no
/// ceiling. The self-administration refusal is checked first.
/// </para>
/// <para>
/// <b>A listed role goes only to an enrolled account.</b> When a requested role is listed under
/// <c>Authorization:MultiFactor:RequiredForRoles</c> and the host registers an <see cref="ITwoFactorStore"/>,
/// the role action is refused with 409 and the stable code
/// <c>UserAdministration.SecondFactorNotEnrolled</c> unless the target account has turned on two-factor
/// authentication. It is checked after the privilege ceiling and before anything is saved.
/// </para>
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
/// <param name="administration">The app's user-administration service.</param>
[HasPermission(AdministrationPermissions.ManageUsers)]
public abstract partial class UsersAdminControllerBase<TUserDto>(
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
    /// Locks an account, so it can no longer sign in. Refused with 403 for the caller's own account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A service derived from <see cref="UserAdministrationServiceBase{TUser, TUserDto}"/> makes the lock
    /// take effect at once: it writes the soft-deleted user marker, so every host refuses the access
    /// tokens the account already holds, and revokes its live refresh sessions, so no new one is minted.
    /// A service that only revokes the sessions leaves each issued access token working until it expires.
    /// </para>
    /// <para>
    /// An administrator who locks their own account revokes their own sessions and can no longer sign
    /// in to undo it, so the self case is refused before the service runs, with the
    /// <c>UserAdministration.Self</c> error code.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account to lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpPost("{userId}/lock")]
    [NonIdempotent("Locking is a state setter, not a create: a second lock re-evaluates the account's current state, and a cached 204 would answer a later unlock-then-lock cycle with a state the account is not in.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> LockAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        if (IsCaller(userId))
        {
            return HandleFailure([SelfAdministrationRefused()]);
        }

        var ceiling = await CheckTargetCeilingAsync(userId, cancellationToken).ConfigureAwait(false);
        if (ceiling.IsFailure)
        {
            return HandleFailure(ceiling.Errors);
        }

        var result = await Administration.SetLockedAsync(userId, locked: true, cancellationToken).ConfigureAwait(false);

        return Completed(result, "lock", userId);
    }

    /// <summary>
    /// Unlocks an account. Refused with 403 for the caller's own account, as <see cref="LockAsync"/> is.
    /// </summary>
    /// <param name="userId">The account to unlock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpPost("{userId}/unlock")]
    [NonIdempotent("Unlocking is a state setter, not a create: a second unlock re-evaluates the account's current state, and a cached 204 would answer a later lock-then-unlock cycle with a state the account is not in.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> UnlockAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        if (IsCaller(userId))
        {
            return HandleFailure([SelfAdministrationRefused()]);
        }

        var ceiling = await CheckTargetCeilingAsync(userId, cancellationToken).ConfigureAwait(false);
        if (ceiling.IsFailure)
        {
            return HandleFailure(ceiling.Errors);
        }

        var result = await Administration.SetLockedAsync(userId, locked: false, cancellationToken).ConfigureAwait(false);

        return Completed(result, "unlock", userId);
    }

    /// <summary>
    /// Replaces the set of roles an account holds. Refused with 403 for the caller's own account, so an
    /// administrator cannot drop the permissions that let them manage users and roles.
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

        if (IsCaller(userId))
        {
            return HandleFailure([SelfAdministrationRefused()]);
        }

        var ceiling = await CheckRoleChangeCeilingAsync(userId, request.Roles, cancellationToken).ConfigureAwait(false);
        if (ceiling.IsFailure)
        {
            return HandleFailure(ceiling.Errors);
        }

        var enrollment = await CheckSecondFactorEnrollmentAsync(userId, request.Roles, cancellationToken).ConfigureAwait(false);
        if (enrollment.IsFailure)
        {
            return HandleFailure(enrollment.Errors);
        }

        var result = await Administration
            .SetRolesAsync(userId, request.Roles, cancellationToken)
            .ConfigureAwait(false);

        return Completed(result, "set-roles", userId);
    }

    /// <summary>
    /// The refusal the lock, unlock and role actions return for the caller's own account: a
    /// <see cref="ErrorType.Forbidden"/> error, so it maps to 403 like the other administration refusals.
    /// </summary>
    /// <returns>The <c>UserAdministration.Self</c> error.</returns>
    private static Error SelfAdministrationRefused() =>
        Error.Forbidden(
            "UserAdministration.Self",
            "You cannot lock, unlock or change the roles of your own account.",
            "userId");

    /// <summary>
    /// The refusal the lock, unlock and role actions return when the requested role, or the target
    /// account's current role, grants a permission the caller does not hold. It names no permission.
    /// </summary>
    /// <returns>The <c>UserAdministration.PrivilegeCeiling</c> error.</returns>
    private static Error PrivilegeCeilingRefused() =>
        Error.Forbidden(
            "UserAdministration.PrivilegeCeiling",
            "You cannot assign this role, or administer this account, because it carries permissions you do not hold.",
            "userId");

    /// <summary>
    /// The refusal the role action returns when a requested role is listed under
    /// <see cref="MultiFactorAuthorizationSettings.RequiredForRoles"/> and the target account has not
    /// turned on two-factor authentication.
    /// </summary>
    /// <returns>The <c>UserAdministration.SecondFactorNotEnrolled</c> error.</returns>
    private static Error SecondFactorNotEnrolledRefused() =>
        Error.Conflict(
            "UserAdministration.SecondFactorNotEnrolled",
            "The account must turn on two-factor authentication before it can be given this role.",
            "roles");

    [LoggerMessage(Level = LogLevel.Information, Message = "Administrator {ActorUserId} applied {Action} to user {UserId}")]
    private static partial void LogAdministered(ILogger logger, string action, UserIdentifierType userId, UserIdentifierType? actorUserId);

    /// <summary>
    /// The ceiling for a role change: the one requested role first (no load), then the target
    /// account's current role. A request that names zero roles or several passes untouched, so the
    /// service's exactly-one-role refusal is what the caller receives, with no load ahead of it.
    /// </summary>
    /// <param name="userId">The account the request targets.</param>
    /// <param name="roles">The requested roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the refusal.</returns>
    private async Task<Result> CheckRoleChangeCeilingAsync(
        UserIdentifierType userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken)
    {
        if (roles is not [var requestedRole])
        {
            return Result.Success();
        }

        return CallerHoldsEveryPermissionOf(requestedRole)
            ? await CheckTargetCeilingAsync(userId, cancellationToken).ConfigureAwait(false)
            : Result.Failure(PrivilegeCeilingRefused());
    }

    /// <summary>
    /// The ceiling on the target account: refuses when its current role grants a permission the
    /// caller does not hold. A host with no permission registry applies no ceiling, and an unknown
    /// account returns the service's not-found failure.
    /// </summary>
    /// <param name="userId">The account the request targets.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the refusal.</returns>
    private async Task<Result> CheckTargetCeilingAsync(UserIdentifierType userId, CancellationToken cancellationToken)
    {
        if (PermissionRegistry is null)
        {
            return Result.Success();
        }

        var role = await Administration.GetRoleAsync(userId, cancellationToken).ConfigureAwait(false);
        if (role.IsFailure)
        {
            return Result.Failure(role.Errors);
        }

        return CallerHoldsEveryPermissionOf(role.Value!)
            ? Result.Success()
            : Result.Failure(PrivilegeCeilingRefused());
    }

    /// <summary>
    /// Whether the caller holds every permission <paramref name="role"/> grants, each evaluated as the
    /// permission authorization handler evaluates it. With no permission registry it is always true.
    /// </summary>
    /// <param name="role">The requested role, or the target account's current role.</param>
    /// <returns><see langword="true"/> when the caller holds them all.</returns>
    private bool CallerHoldsEveryPermissionOf(string role) =>
        PermissionRegistry is not { } registry
        || registry.GetPermissions(role).All(permission => PermissionAuthorizationHandler.Holds(User, registry, permission));

    /// <summary>
    /// The enrollment check for a role change: refuses when a requested role is listed under
    /// <see cref="MultiFactorAuthorizationSettings.RequiredForRoles"/>, the host registers an
    /// <see cref="ITwoFactorStore"/>, and the target account's second factor is not on (or the store
    /// holds no state for it). With the list empty, with no store, or for roles that are not listed it
    /// passes without a load.
    /// </summary>
    /// <param name="userId">The account the request targets.</param>
    /// <param name="roles">The requested roles.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the refusal.</returns>
    private async Task<Result> CheckSecondFactorEnrollmentAsync(
        UserIdentifierType userId,
        IReadOnlyList<string>? roles,
        CancellationToken cancellationToken)
    {
        var services = HttpContext?.RequestServices;
        if (services is null
            || !RequestsAListedRole(services, roles)
            || services.GetService<ITwoFactorStore>() is not { } twoFactorStore)
        {
            return Result.Success();
        }

        var state = await twoFactorStore.GetAsync(userId, cancellationToken).ConfigureAwait(false);

        return state?.IsTwoFactorEnabled == true
            ? Result.Success()
            : Result.Failure(SecondFactorNotEnrolledRefused());
    }

    /// <summary>Whether any requested role is listed under the role-level second-factor rule.</summary>
    /// <param name="services">The request's services.</param>
    /// <param name="roles">The requested roles.</param>
    /// <returns><see langword="true"/> when a requested role is listed (blank entries ignored, case-insensitive).</returns>
    private static bool RequestsAListedRole(IServiceProvider services, IReadOnlyList<string>? roles) =>
        roles is { Count: > 0 }
        && services.GetService<IOptions<MultiFactorAuthorizationSettings>>()?.Value.RequiredForRoles is { Count: > 0 } listed
        && roles.Any(role => listed.Any(entry =>
            !string.IsNullOrWhiteSpace(entry) && string.Equals(entry, role, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The host's permission registry, or <see langword="null"/> on a role-only host.</summary>
    private IPermissionRegistry? PermissionRegistry => HttpContext?.RequestServices?.GetService<IPermissionRegistry>();

    /// <summary>
    /// Maps the service result to the response and, on success, logs one line naming the action, the
    /// target account and the acting administrator.
    /// </summary>
    /// <param name="result">The service result.</param>
    /// <param name="action">The action applied.</param>
    /// <param name="userId">The account the request targeted.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    private ActionResult Completed(Result result, string action, UserIdentifierType userId)
    {
        if (result.IsFailure)
        {
            return HandleFailure(result.Errors);
        }

        if (HttpContext?.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(GetType()) is { } logger)
        {
            var actorUserId = User.GetUserId();
            LogAdministered(logger, action, userId, actorUserId);
        }

        return NoContent();
    }

    /// <summary>Whether <paramref name="userId"/> is the signed-in caller's own account.</summary>
    /// <param name="userId">The account the request targets.</param>
    /// <returns><see langword="true"/> when the caller's subject resolves to the same id.</returns>
    private bool IsCaller(UserIdentifierType userId) =>
        User.GetUserId() is { } callerId
        && EqualityComparer<UserIdentifierType>.Default.Equals(callerId, userId);
}
