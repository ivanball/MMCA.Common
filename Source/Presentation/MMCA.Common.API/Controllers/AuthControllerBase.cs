using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Base controller for authentication endpoints (login, register, refresh, revoke, password change).
/// Downstream modules inherit and apply route/version attributes. Override <see cref="RegisterAsync"/>
/// to inject additional context (e.g., client IP for rate limiting).
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
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
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
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status201Created)]
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

    /// <summary>
    /// Changes the current user's password after verifying the existing password.
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

        var result = await AuthenticationService.ChangePasswordAsync(userId.Value, request, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }
}
