using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.Users;
using MMCA.Common.Application.Users.UseCases.GetPreferences;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Adds the self-service account endpoints (<c>PUT password</c>, <c>PUT preferences</c>,
/// <c>GET preferences</c>) on top of <see cref="AuthControllerBase"/>. The app Identity modules
/// carried line-identical copies of all three actions; only the command records they constructed
/// differed.
/// </summary>
/// <remarks>
/// <para>
/// The commands stay app-side (<typeparamref name="TChangePasswordCommand"/> /
/// <typeparamref name="TChangePreferencesCommand"/>): ADC marks them <c>ICacheInvalidating</c> with a
/// cache prefix built from its own <c>User</c> type and Store does not, so a single shared record
/// could not preserve both behaviors. This base therefore never constructs a command itself: the
/// derived controller supplies one through <see cref="CreateChangePasswordCommand"/> and
/// <see cref="CreateChangePreferencesCommand"/> (two one-line overrides), and the base reads it back
/// only through <see cref="IUserScopedCommand{TRequest}"/>. The preferences QUERY has no such
/// per-app detail, so <see cref="GetUserPreferencesQuery"/> is shared and constructed here.
/// </para>
/// <para>
/// Inheriting this base instead of <see cref="AuthControllerBase"/> is purely additive: every
/// inherited login/register/refresh/revoke action, including the default per-IP throttling and the
/// ability to override <c>RegisterAsync</c> or attach an extra
/// <c>[EnableRateLimiting]</c> policy app-side, behaves exactly as before.
/// </para>
/// </remarks>
/// <typeparam name="TChangePasswordCommand">The app's change-password command record.</typeparam>
/// <typeparam name="TChangePreferencesCommand">The app's change-preferences command record.</typeparam>
public abstract class UserAccountAuthControllerBase<TChangePasswordCommand, TChangePreferencesCommand>(
    IAuthenticationService authenticationService,
    ICurrentUserService currentUserService,
    ICommandHandler<TChangePasswordCommand, Result> changePasswordHandler,
    ICommandHandler<TChangePreferencesCommand, Result> changePreferencesHandler,
    IQueryHandler<GetUserPreferencesQuery, Result<UserPreferencesResponse>> getUserPreferencesHandler)
    : AuthControllerBase(authenticationService, currentUserService)
    where TChangePasswordCommand : IUserScopedCommand<ChangePasswordRequest>
    where TChangePreferencesCommand : IUserScopedCommand<ChangePreferencesRequest>
{
    /// <summary>The change-password command handler for this controller.</summary>
    protected ICommandHandler<TChangePasswordCommand, Result> ChangePasswordHandler { get; } = changePasswordHandler;

    /// <summary>The change-preferences command handler for this controller.</summary>
    protected ICommandHandler<TChangePreferencesCommand, Result> ChangePreferencesHandler { get; } = changePreferencesHandler;

    /// <summary>The preferences query handler for this controller.</summary>
    protected IQueryHandler<GetUserPreferencesQuery, Result<UserPreferencesResponse>> GetUserPreferencesHandler { get; } = getUserPreferencesHandler;

    /// <summary>
    /// Builds the app's change-password command. Implement as
    /// <c>=&gt; new(userId, request);</c> in the derived controller.
    /// </summary>
    /// <param name="userId">The authenticated user the command targets.</param>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TChangePasswordCommand CreateChangePasswordCommand(
        UserIdentifierType userId,
        ChangePasswordRequest request);

    /// <summary>
    /// Builds the app's change-preferences command. Implement as
    /// <c>=&gt; new(userId, request);</c> in the derived controller.
    /// </summary>
    /// <param name="userId">The authenticated user the command targets.</param>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TChangePreferencesCommand CreateChangePreferencesCommand(
        UserIdentifierType userId,
        ChangePreferencesRequest request);

    /// <summary>
    /// Changes the current user's password after verifying the existing password. Dispatches the
    /// app's change-password command handler directly (through the decorator pipeline) rather than
    /// brokering it via the authentication service.
    /// </summary>
    [HttpPut("password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> ChangePasswordAsync(
        [FromBody] ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserService.UserId;
        if (userId is null)
            return Unauthorized();

        var result = await ChangePasswordHandler
            .HandleAsync(CreateChangePasswordCommand(userId.Value, request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }

    /// <summary>
    /// Persists the current user's UI culture/theme preferences (ADR-027 / ADR-028) so they follow the
    /// user across devices. A null field leaves that preference unchanged.
    /// </summary>
    [HttpPut("preferences")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> ChangePreferencesAsync(
        [FromBody] ChangePreferencesRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserService.UserId;
        if (userId is null)
            return Unauthorized();

        var result = await ChangePreferencesHandler
            .HandleAsync(CreateChangePreferencesCommand(userId.Value, request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }

    /// <summary>
    /// Returns the current user's stored UI culture/theme preferences (ADR-027 / ADR-028), used at login
    /// to apply a returning user's choice across devices.
    /// </summary>
    [HttpGet("preferences")]
    [Authorize]
    [ProducesResponseType(typeof(UserPreferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<UserPreferencesResponse>> GetPreferencesAsync(
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserService.UserId;
        if (userId is null)
            return Unauthorized();

        var result = await GetUserPreferencesHandler
            .HandleAsync(new GetUserPreferencesQuery(userId.Value), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }
}
