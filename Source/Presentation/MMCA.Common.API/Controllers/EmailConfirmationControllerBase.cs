using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// The anonymous email-confirmation endpoints (<c>POST send-email-confirmation</c>,
/// <c>POST confirm-email</c>, ADR-116), the routes <c>EmailConfirmationUIService</c> calls. A sibling
/// of <see cref="AuthControllerBase"/> and <see cref="PasswordResetAuthControllerBase{TForgotPasswordCommand, TResetPasswordCommand}"/>
/// rather than an addition to either: the apps' own <c>AuthController</c> already occupies that
/// single-inheritance chain, so confirmation ships as a separate controller the app routes to the
/// same <c>Auth</c> prefix, which reaches the gateway through the existing <c>/Auth</c> route.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both actions are anonymous by necessity</b>: the confirmation link is followed from a mail
/// client with no session, and an unconfirmed account may be unable to sign in at all when
/// confirmation is required. They carry <see cref="WebApplicationBuilderExtensions.RateLimitPolicyAuthIp"/>
/// for the same reason the login and recovery actions do, and the framework's anonymous-endpoint
/// architecture gate lists them explicitly.
/// </para>
/// <para>
/// <b>Send answers 202 on every well-formed request.</b> The handler treats an unknown address and an
/// already-confirmed one as success, so the response never reveals which addresses hold accounts; only
/// a malformed payload reaches 400, through the request validator.
/// </para>
/// <para>
/// The commands stay app-side (<typeparamref name="TSendCommand"/> / <typeparamref name="TConfirmCommand"/>),
/// matching the password-recovery base: the derived controller supplies each through a one-line
/// factory and the base reads them back only through <see cref="ICommandWithRequest{TRequest}"/>.
/// Route and API version stay on the derived controller.
/// </para>
/// </remarks>
/// <typeparam name="TSendCommand">The app's send-email-confirmation command record.</typeparam>
/// <typeparam name="TConfirmCommand">The app's confirm-email command record.</typeparam>
public abstract class EmailConfirmationControllerBase<TSendCommand, TConfirmCommand>(
    ICommandHandler<TSendCommand, Result> sendHandler,
    ICommandHandler<TConfirmCommand, Result> confirmHandler) : ApiControllerBase
    where TSendCommand : ICommandWithRequest<SendEmailConfirmationRequest>
    where TConfirmCommand : ICommandWithRequest<ConfirmEmailRequest>
{
    /// <summary>The send-email-confirmation command handler for this controller.</summary>
    protected ICommandHandler<TSendCommand, Result> SendHandler { get; } = sendHandler;

    /// <summary>The confirm-email command handler for this controller.</summary>
    protected ICommandHandler<TConfirmCommand, Result> ConfirmHandler { get; } = confirmHandler;

    /// <summary>
    /// Builds the app's send-email-confirmation command. Implement as <c>=&gt; new(request);</c> in
    /// the derived controller.
    /// </summary>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TSendCommand CreateSendCommand(SendEmailConfirmationRequest request);

    /// <summary>
    /// Builds the app's confirm-email command. Implement as <c>=&gt; new(request);</c> in the derived
    /// controller.
    /// </summary>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TConfirmCommand CreateConfirmCommand(ConfirmEmailRequest request);

    /// <summary>
    /// Sends a confirmation link to an address. Answers 202 on a well-formed request, whether or not
    /// the address holds an account and whether or not it is already confirmed.
    /// </summary>
    /// <param name="request">The address to send to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted, or a Problem Details failure for a malformed payload.</returns>
    [HttpPost("send-email-confirmation")]
    [Idempotent]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> SendEmailConfirmationAsync(
        [FromBody] SendEmailConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await SendHandler
            .HandleAsync(CreateSendCommand(request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Accepted();
    }

    /// <summary>
    /// Redeems a confirmation token. The link carries the address and the token in the URI FRAGMENT,
    /// so the page reads them client-side and posts them here; neither ever reaches a server log or a
    /// Referer header.
    /// </summary>
    /// <param name="request">The address and its confirmation token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpPost("confirm-email")]
    [Idempotent]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> ConfirmEmailAsync(
        [FromBody] ConfirmEmailRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ConfirmHandler
            .HandleAsync(CreateConfirmCommand(request), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }
}
