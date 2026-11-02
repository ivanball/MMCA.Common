using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users.UseCases.SignInLinks;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// The anonymous sign-in-link endpoints (<c>POST sign-in-link</c>, <c>POST sign-in-link/redeem</c>,
/// ADR-116), the routes <c>SignInLinkUIService</c> calls. A sibling of
/// <see cref="EmailConfirmationControllerBase{TSendCommand, TConfirmCommand}"/> for the same reason
/// that one is: the app's own <c>AuthController</c> already occupies the auth controller chain, so
/// this ships as a separate controller the app routes to the same <c>Auth</c> prefix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both actions are anonymous by necessity</b> (the caller is signing in) and carry
/// <see cref="WebApplicationBuilderExtensions.RateLimitPolicyAuthIp"/>, as login does; the framework's
/// anonymous-endpoint architecture gate lists them explicitly.
/// </para>
/// <para>
/// <b>Send answers 202 on every well-formed request</b>, whether or not the address holds an account,
/// so the response never reveals which addresses do. Only a malformed payload reaches 400.
/// </para>
/// </remarks>
/// <typeparam name="TSendCommand">The app's send-sign-in-link command record.</typeparam>
/// <typeparam name="TRedeemCommand">The app's redeem-sign-in-link command record.</typeparam>
public abstract class SignInLinkControllerBase<TSendCommand, TRedeemCommand>(
    ICommandHandler<TSendCommand, Result> sendHandler,
    ICommandHandler<TRedeemCommand, Result<AuthenticationResponse>> redeemHandler) : ApiControllerBase
    where TSendCommand : ICommandWithRequest<SendSignInLinkRequest>
    where TRedeemCommand : IRedeemSignInLinkCommand
{
    /// <summary>The send-sign-in-link command handler for this controller.</summary>
    protected ICommandHandler<TSendCommand, Result> SendHandler { get; } = sendHandler;

    /// <summary>The redeem-sign-in-link command handler for this controller.</summary>
    protected ICommandHandler<TRedeemCommand, Result<AuthenticationResponse>> RedeemHandler { get; } = redeemHandler;

    /// <summary>
    /// The caller's IP, recorded on the opened session. Reads the connection's remote address, which
    /// behind a proxy is the forwarded value only when the host has configured <c>UseForwardedHeaders</c>.
    /// </summary>
    protected string? ClientIpAddress => HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>The caller's user-agent, recorded on the opened session.</summary>
    protected string? ClientUserAgent => HttpContext?.Request.Headers.UserAgent.ToString();

    /// <summary>
    /// Builds the app's send-sign-in-link command. Implement as <c>=&gt; new(request);</c>.
    /// </summary>
    /// <param name="request">The validated request payload.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TSendCommand CreateSendCommand(SendSignInLinkRequest request);

    /// <summary>
    /// Builds the app's redeem-sign-in-link command. Implement as
    /// <c>=&gt; new(request, ipAddress, userAgent);</c>.
    /// </summary>
    /// <param name="request">The validated request payload.</param>
    /// <param name="ipAddress">The caller's IP.</param>
    /// <param name="userAgent">The caller's user-agent.</param>
    /// <returns>The app command to dispatch through the decorator pipeline.</returns>
    protected abstract TRedeemCommand CreateRedeemCommand(RedeemSignInLinkRequest request, string? ipAddress, string? userAgent);

    /// <summary>
    /// Emails a single-use sign-in link to an address. Answers 202 on a well-formed request, whether
    /// or not the address holds an account.
    /// </summary>
    /// <param name="request">The address to send to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Accepted, or a Problem Details failure for a malformed payload.</returns>
    [HttpPost("sign-in-link")]
    [Idempotent]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> SendSignInLinkAsync(
        [FromBody] SendSignInLinkRequest request,
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
    /// Redeems a sign-in link and returns the token pair. The link carries the address and the token
    /// in the URI FRAGMENT, so the page reads them client-side and posts them here; neither ever
    /// reaches a server log or a Referer header.
    /// </summary>
    /// <param name="request">The address and its sign-in token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token pair, or a Problem Details failure.</returns>
    [HttpPost("sign-in-link/redeem")]
    [NonIdempotent("Redeeming issues a token pair. A replayed response would hand a retrying client the tokens minted for an earlier call; the token is single-use, so a retry is refused instead, which is the correct answer.")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> RedeemSignInLinkAsync(
        [FromBody] RedeemSignInLinkRequest request,
        CancellationToken cancellationToken)
    {
        var result = await RedeemHandler
            .HandleAsync(CreateRedeemCommand(request, ClientIpAddress, ClientUserAgent), cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }
}
