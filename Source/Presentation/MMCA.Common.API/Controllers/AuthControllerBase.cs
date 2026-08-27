using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Idempotency;
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
    /// The caller's IP, recorded on the refresh session and used for the BR-213 registration rate
    /// limit. Reads the connection's remote address, which behind a proxy is the forwarded value
    /// only when the host has configured <c>UseForwardedHeaders</c>.
    /// </summary>
    protected string? ClientIpAddress => HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// The caller's user-agent, recorded on the refresh session so a device list can name the
    /// session a user is looking at. Purely informational: nothing validates against it.
    /// </summary>
    protected string? ClientUserAgent => HttpContext?.Request.Headers.UserAgent.ToString();

    /// <summary>
    /// Authenticates a user with email and password, returning access and refresh tokens.
    /// </summary>
    [HttpPost("login")]
    [NonIdempotent("Login issues a token pair. A replayed response would hand a retrying client the tokens minted for an earlier call, extending the lifetime of credentials the caller may already have discarded.")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> LoginAsync(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await AuthenticationService
            .LoginAsync(request, ClientIpAddress, ClientUserAgent, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Registers a new user account and returns authentication tokens.
    /// Override in derived controllers to pass additional context (e.g., client IP).
    /// </summary>
    [HttpPost("register")]
    [Idempotent]
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
        var result = await AuthenticationService
            .RegisterAsync(request, ClientIpAddress, ClientUserAgent, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Exchanges an expired access token and valid refresh token for a new token pair.
    /// </summary>
    [HttpPost("refresh")]
    [NonIdempotent("Refresh rotates the refresh token and issues a new pair. Replaying a stored response would return a token the rotation has already invalidated, so the client would be handed dead credentials instead of live ones.")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> RefreshAsync(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await AuthenticationService
            .RefreshTokenAsync(request, ClientIpAddress, ClientUserAgent, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }

    /// <summary>
    /// Revokes the current user's refresh sessions, effectively logging them out.
    /// <para>
    /// The endpoint carries no body, so it cannot name the device it is being called from: it signs
    /// the user out everywhere, which is what the single-token predecessor did and what a caller with
    /// no way to identify its own session should get. A consumer that wants per-device sign-out calls
    /// <see cref="IAuthenticationService.RevokeTokenAsync"/> with the client's refresh token from its
    /// own action.
    /// </para>
    /// </summary>
    [HttpPost("revoke")]
    [NonIdempotent("Revocation must reach the store on every call. Replaying a cached 204 would report success for a revoke that never ran, leaving a refresh token live after the user asked for it to be killed.")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public virtual async Task<ActionResult> RevokeAsync(CancellationToken cancellationToken)
    {
        var userId = CurrentUserService.UserId;
        if (userId is null)
            return Unauthorized();

        var result = await AuthenticationService
            .RevokeAllSessionsAsync(userId.Value, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }
}
