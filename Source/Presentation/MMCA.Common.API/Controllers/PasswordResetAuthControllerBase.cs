using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// The anonymous password-recovery endpoints (<c>POST forgot-password</c>,
/// <c>POST reset-password</c>). A sibling of <see cref="AuthControllerBase"/> rather than an
/// addition to it: the apps' own <c>AuthController</c> already occupies that single-inheritance
/// chain, so recovery ships as a separate controller the app routes to the same <c>Auth</c> prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both actions are anonymous by necessity</b>: the caller has lost the credential, so requiring
/// one would be circular. They carry
/// <see cref="WebApplicationBuilderExtensions.RateLimitPolicyAuthIp"/> for the same reason the
/// login and register actions do, and the framework's anonymous-endpoint architecture gate lists
/// them explicitly.
/// </para>
/// <para>
/// <b>Forgot-password always answers 202.</b> The handler treats an unknown address, a throttled
/// request and a failed send as success, so the response never reveals which addresses hold
/// accounts. Only a malformed payload reaches 400, through the request validator.
/// </para>
/// <para>
/// The commands stay app-side (<typeparamref name="TForgotPasswordCommand"/> /
/// <typeparamref name="TResetPasswordCommand"/>), matching
/// <see cref="UserAccountAuthControllerBase{TChangePasswordCommand, TChangePreferencesCommand}"/>:
/// ADC marks its reset command <c>ICacheInvalidating</c> and Store does not. The derived controller
/// supplies each through a one-line factory and the base reads them back only through
/// <see cref="ICommandWithRequest{TRequest}"/>.
/// </para>
/// </remarks>
/// <typeparam name="TForgotPasswordCommand">The app's forgot-password command record.</typeparam>
/// <typeparam name="TResetPasswordCommand">The app's reset-password command record.</typeparam>
public abstract class PasswordResetAuthControllerBase<TForgotPasswordCommand, TResetPasswordCommand>(
    ICommandHandler<TForgotPasswordCommand, Result> forgotPasswordHandler,
    ICommandHandler<TResetPasswordCommand, Result> resetPasswordHandler) : ApiControllerBase
    where TForgotPasswordCommand : ICommandWithRequest<ForgotPasswordRequest>
    where TResetPasswordCommand : ICommandWithRequest<ResetPasswordRequest>
{
    /// <summary>The forgot-password command handler for this controller.</summary>
    protected ICommandHandler<TForgotPasswordCommand, Result> ForgotPasswordHandler { get; } = forgotPasswordHandler;

    /// <summary>The reset-password command handler for this controller.</summary>
    protected ICommandHandler<TResetPasswordCommand, Result> ResetPasswordHandler { get; } = resetPasswordHandler;

    /// <summary>
    /// Builds the app's forgot-password command. Implement as <c>=&gt; new(request);</c> in the
    /// derived controller.
    /// </summary>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TForgotPasswordCommand CreateForgotPasswordCommand(ForgotPasswordRequest request);

    /// <summary>
    /// Builds the app's reset-password command. Implement as <c>=&gt; new(request);</c> in the
    /// derived controller.
    /// </summary>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TResetPasswordCommand CreateResetPasswordCommand(ResetPasswordRequest request);

    /// <summary>
    /// Starts a password reset: emails a single-use reset token to the address when it belongs to an
    /// account. Always answers 202 on a well-formed request, whether or not the account exists.
    /// </summary>
    [HttpPost("forgot-password")]
    [Idempotent]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> ForgotPasswordAsync(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ForgotPasswordHandler
            .HandleAsync(CreateForgotPasswordCommand(request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Accepted();
    }

    /// <summary>
    /// Completes a password reset by redeeming the single-use token. Every rejection collapses to
    /// one 401, so the response reveals nothing about which addresses or tokens exist.
    /// </summary>
    [HttpPost("reset-password")]
    [Idempotent]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> ResetPasswordAsync(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ResetPasswordHandler
            .HandleAsync(CreateResetPasswordCommand(request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }
}
