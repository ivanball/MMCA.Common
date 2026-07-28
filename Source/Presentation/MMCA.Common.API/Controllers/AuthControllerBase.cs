using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Base controller for authentication endpoints (login, register, refresh, revoke).
/// Downstream modules inherit and apply route/version attributes. Override <see cref="RegisterAsync"/>
/// to inject additional context (e.g., client IP for rate limiting). Password change is owned by the
/// derived controller, which dispatches the ChangePassword command handler directly.
/// <para>
/// <b>Anti-spray throttling is on by default.</b> <see cref="LoginAsync"/> and
/// <see cref="RegisterAsync"/> carry
/// <see cref="WebApplicationBuilderExtensions.RateLimitPolicyAuthIp"/>, so any consumer inheriting
/// this base gets per-IP protection without opting in. That default exists because the alternative
/// failed in practice: the policy shipped in the framework while each app was left to attach it,
/// and an app that simply inherited these actions silently had no spray protection at all. The
/// global limiter deliberately no-ops for anonymous traffic and account lockout is per-email, so a
/// spray (one password, many emails) from one source is otherwise unthrottled.
/// </para>
/// <para>
/// <b><see cref="RefreshAsync"/> is deliberately NOT throttled.</b> Refresh is automatic and
/// periodic rather than user-initiated, and Blazor Server circuits issue it server-side, so every
/// Server-circuit user shares the UI host's IP. A per-IP window there would throttle ordinary token
/// renewal for everyone behind that host. Refresh tokens are also high-entropy, so brute force is
/// not the threat password spraying is.
/// </para>
/// <para>
/// Consumers must call <c>AddCommonRateLimiting()</c> (which registers the policy). A consumer that
/// inherits this base without it fails at startup on an unregistered policy, which is the loud
/// failure rather than the silent one.
/// </para>
/// </summary>
public abstract class AuthControllerBase(
    IAuthenticationService authenticationService,
    ICurrentUserService currentUserService) : ApiControllerBase
{
    /// <summary>The authentication service for this controller.</summary>
    protected IAuthenticationService AuthenticationService { get; } = authenticationService;

    /// <summary>The current user service for this controller.</summary>
    protected ICurrentUserService CurrentUserService { get; } = currentUserService;

    /// <summary>
    /// Authenticates a user with email and password, returning access and refresh tokens.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await AuthenticationService.LoginAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Registers a new user account and returns authentication tokens.
    /// Override in derived controllers to pass additional context (e.g., client IP).
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> RegisterAsync(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await AuthenticationService.RegisterAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Exchanges an expired access token and valid refresh token for a new token pair.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await AuthenticationService.RefreshTokenAsync(request, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Revokes the current user's refresh token, effectively logging them out.
    /// </summary>
    [HttpPost("revoke")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult> RevokeAsync(CancellationToken cancellationToken)
    {
        var userId = CurrentUserService.UserId;
        if (userId is null)
            return Unauthorized();

        var result = await AuthenticationService.RevokeTokenAsync(userId.Value, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }
}
