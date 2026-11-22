using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Controllers.TwoFactor;

/// <summary>
/// The self-service two-factor endpoints (ADR-116): read the caller's state, start and confirm an
/// enrollment, replace the recovery codes, and turn the second factor off. The app's sealed subclass
/// carries the routing attributes, <c>[ApiController][Route("auth/two-factor")][ApiVersion("1.0")]</c>,
/// and the four one-line command factories.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every action is plain <c>[Authorize]</c>, never a permission.</b> A host that requires a second
/// factor for a role (<c>Authorization:MultiFactor:RequiredForRoles</c>) refuses that role's permission
/// endpoints until the session carries <c>mfa</c>; these endpoints stay reachable so the account can
/// enroll in the first place.
/// </para>
/// <para>
/// <b>Starting and confirming enrollment need a recent sign-in</b> (<see cref="PasskeyStepUp"/>, the
/// session's <c>auth_time</c> within the last ten minutes), because enrollment adds a factor the
/// account's sign-in will then demand. A refresh never renews <c>auth_time</c>.
/// </para>
/// <para>
/// <b>Turning the factor on or off ends the account's sessions.</b> A confirmed enrollment and a
/// disable revoke every refresh session of the account (<see cref="IAuthenticationService.RevokeAllSessionsAsync"/>,
/// resolved from the request's services), so no session signed in under the earlier state keeps
/// refreshing. The response is returned either way; regenerating recovery codes ends nothing.
/// </para>
/// <para>
/// The commands stay app-side, as on <see cref="UserAccountAuthControllerBase{TChangePasswordCommand, TChangePreferencesCommand}"/>:
/// the base reads them back only through <see cref="IUserScopedRequest"/> and
/// <see cref="IUserScopedCommand{TRequest}"/>. A machine client is refused on every action.
/// </para>
/// </remarks>
/// <typeparam name="TBeginCommand">The app's begin-enrollment command record.</typeparam>
/// <typeparam name="TConfirmCommand">The app's confirm-enrollment command record.</typeparam>
/// <typeparam name="TDisableCommand">The app's disable command record.</typeparam>
/// <typeparam name="TRegenerateCommand">The app's regenerate-recovery-codes command record.</typeparam>
/// <param name="beginHandler">Starts an enrollment and returns the shared secret.</param>
/// <param name="confirmHandler">Confirms an enrollment with a code and returns the recovery codes.</param>
/// <param name="disableHandler">Turns the second factor off after a code verifies.</param>
/// <param name="regenerateHandler">Replaces the recovery codes after a code verifies.</param>
/// <param name="store">The app's two-factor store, read for the status action.</param>
[RefuseMachineClients]
public abstract partial class TwoFactorControllerBase<TBeginCommand, TConfirmCommand, TDisableCommand, TRegenerateCommand>(
    ICommandHandler<TBeginCommand, Result<TwoFactorSetupResponse>> beginHandler,
    ICommandHandler<TConfirmCommand, Result<TwoFactorRecoveryCodesResponse>> confirmHandler,
    ICommandHandler<TDisableCommand, Result> disableHandler,
    ICommandHandler<TRegenerateCommand, Result<TwoFactorRecoveryCodesResponse>> regenerateHandler,
    ITwoFactorStore store) : ApiControllerBase
    where TBeginCommand : IUserScopedRequest
    where TConfirmCommand : IUserScopedCommand<TwoFactorCodeRequest>
    where TDisableCommand : IUserScopedCommand<TwoFactorCodeRequest>
    where TRegenerateCommand : IUserScopedCommand<TwoFactorCodeRequest>
{
    /// <summary>The begin-enrollment command handler.</summary>
    protected ICommandHandler<TBeginCommand, Result<TwoFactorSetupResponse>> BeginHandler { get; } = beginHandler;

    /// <summary>The confirm-enrollment command handler.</summary>
    protected ICommandHandler<TConfirmCommand, Result<TwoFactorRecoveryCodesResponse>> ConfirmHandler { get; } = confirmHandler;

    /// <summary>The disable command handler.</summary>
    protected ICommandHandler<TDisableCommand, Result> DisableHandler { get; } = disableHandler;

    /// <summary>The regenerate-recovery-codes command handler.</summary>
    protected ICommandHandler<TRegenerateCommand, Result<TwoFactorRecoveryCodesResponse>> RegenerateHandler { get; } = regenerateHandler;

    /// <summary>The app's two-factor store.</summary>
    protected ITwoFactorStore Store { get; } = store;

    private DateTimeOffset UtcNow =>
        (HttpContext?.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    /// <summary>Reads the caller's two-factor state.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the factor is on, and how many recovery codes remain.</returns>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(TwoFactorStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<TwoFactorStatusResponse>> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var state = await Store.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return NotFound();
        }

        return Ok(new TwoFactorStatusResponse(
            state.IsTwoFactorEnabled,
            state.IsTwoFactorEnabled ? state.TwoFactorRecoveryCodeHashes.Count : 0));
    }

    /// <summary>Starts an enrollment: returns a fresh shared secret for the authenticator app.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shared secret as a key and as a provisioning URI.</returns>
    [HttpPost("enrollment")]
    [Authorize]
    [NonIdempotent("Each call generates and stores a fresh secret. A replayed response would hand back a secret the store no longer holds.")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TwoFactorSetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TwoFactorSetupResponse>> BeginEnrollmentAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!PasskeyStepUp.IsSatisfiedBy(User, UtcNow))
        {
            return HandleFailure([TwoFactorErrors.RecentSignInRequired(nameof(BeginEnrollmentAsync))]);
        }

        var result = await BeginHandler
            .HandleAsync(CreateBeginEnrollmentCommand(userId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Confirms an enrollment with a code from the app; the second factor is on from here.</summary>
    /// <param name="request">The code minted from the new secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The single-use recovery codes, shown once.</returns>
    [HttpPost("enrollment/confirm")]
    [Authorize]
    [NonIdempotent("A time-based code is accepted once. A replayed response would hand back recovery codes from an earlier confirmation.")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TwoFactorRecoveryCodesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TwoFactorRecoveryCodesResponse>> ConfirmEnrollmentAsync(
        [FromBody] TwoFactorCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!PasskeyStepUp.IsSatisfiedBy(User, UtcNow))
        {
            return HandleFailure([TwoFactorErrors.RecentSignInRequired(nameof(ConfirmEnrollmentAsync))]);
        }

        var result = await ConfirmHandler
            .HandleAsync(CreateConfirmEnrollmentCommand(userId, request), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return HandleFailure(result.Errors);
        }

        await EndSessionsAsync(userId, "enrollment", cancellationToken).ConfigureAwait(false);

        return Ok(result.Value);
    }

    /// <summary>Replaces the recovery codes after a code verifies.</summary>
    /// <param name="request">A code from the app or a recovery code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new single-use recovery codes, shown once.</returns>
    [HttpPost("recovery-codes")]
    [Authorize]
    [NonIdempotent("Each call replaces the whole set of recovery codes. A replayed response would hand back codes the store no longer accepts.")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TwoFactorRecoveryCodesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TwoFactorRecoveryCodesResponse>> RegenerateRecoveryCodesAsync(
        [FromBody] TwoFactorCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await RegenerateHandler
            .HandleAsync(CreateRegenerateRecoveryCodesCommand(userId, request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Turns the second factor off after a code verifies.</summary>
    /// <param name="request">A code from the app or a recovery code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 on success.</returns>
    [HttpPost("disable")]
    [Authorize]
    [NonIdempotent("Disabling spends the presented code and must reach the store. A replayed 204 would hide a refusal of the retry.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> DisableAsync(
        [FromBody] TwoFactorCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await DisableHandler
            .HandleAsync(CreateDisableCommand(userId, request), cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return HandleFailure(result.Errors);
        }

        await EndSessionsAsync(userId, "disable", cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Builds the app's begin-enrollment command. Implement as <c>=&gt; new(userId);</c>.</summary>
    /// <param name="userId">The authenticated user the command targets.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TBeginCommand CreateBeginEnrollmentCommand(UserIdentifierType userId);

    /// <summary>Builds the app's confirm-enrollment command. Implement as <c>=&gt; new(userId, request);</c>.</summary>
    /// <param name="userId">The authenticated user the command targets.</param>
    /// <param name="request">The presented code.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TConfirmCommand CreateConfirmEnrollmentCommand(UserIdentifierType userId, TwoFactorCodeRequest request);

    /// <summary>Builds the app's disable command. Implement as <c>=&gt; new(userId, request);</c>.</summary>
    /// <param name="userId">The authenticated user the command targets.</param>
    /// <param name="request">The presented code.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TDisableCommand CreateDisableCommand(UserIdentifierType userId, TwoFactorCodeRequest request);

    /// <summary>Builds the app's regenerate-recovery-codes command. Implement as <c>=&gt; new(userId, request);</c>.</summary>
    /// <param name="userId">The authenticated user the command targets.</param>
    /// <param name="request">The presented code.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TRegenerateCommand CreateRegenerateRecoveryCodesCommand(UserIdentifierType userId, TwoFactorCodeRequest request);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ending the sessions of user {UserId} after a two-factor {Change} failed ({Reason}); the change itself is saved")]
    private static partial void LogEndSessionsFailed(ILogger logger, UserIdentifierType userId, string change, string reason);

    /// <summary>
    /// Revokes every refresh session of the account once its second factor was turned on or off, so a
    /// session signed in under the earlier state cannot keep refreshing. The change is already saved,
    /// so a failure is logged (no address) and the caller still gets its response. A host without the
    /// framework's <see cref="IAuthenticationService"/> has no sessions to revoke here.
    /// </summary>
    /// <param name="userId">The account whose sessions to end.</param>
    /// <param name="change">The change that was saved, for the log line.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the revocation was attempted.</returns>
    private async Task EndSessionsAsync(UserIdentifierType userId, string change, CancellationToken cancellationToken)
    {
        var services = HttpContext?.RequestServices;
        if (services?.GetService<IAuthenticationService>() is not { } authenticationService)
        {
            return;
        }

        string? failure;
        try
        {
            var revoked = await authenticationService.RevokeAllSessionsAsync(userId, cancellationToken).ConfigureAwait(false);
            failure = revoked.IsFailure ? string.Join(", ", revoked.Errors.Select(error => error.Code)) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.GetType().Name;
        }

        if (failure is not null && services.GetService<ILoggerFactory>()?.CreateLogger(GetType()) is { } logger)
        {
            LogEndSessionsFailed(logger, userId, change, failure);
        }
    }
}
