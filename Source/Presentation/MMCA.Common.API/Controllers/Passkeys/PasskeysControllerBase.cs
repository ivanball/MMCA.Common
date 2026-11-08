using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Controllers.Passkeys;

/// <summary>
/// The passkey endpoints (WebAuthn, ADR-139): registration and management for a signed-in account, and
/// an anonymous sign-in with a discoverable credential. The app's sealed subclass carries the routing
/// attributes, <c>[ApiController][Route("auth/passkeys")][ApiVersion("1.0")]</c>, and needs no body.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off means no routes.</b> Every action answers 404 unless the host called <c>AddPasskeys</c> AND
/// set <c>Passkeys:Enabled</c>, so a consumer can inherit this base ahead of turning the feature on.
/// The ceremony service is resolved per action for that reason: a host without <c>AddPasskeys</c> has
/// none to inject.
/// </para>
/// <para>
/// <b>Registration needs a recent sign-in</b> (<see cref="PasskeyStepUp"/>): an access token under ten
/// minutes old, or one carrying <c>mfa</c>. A long-lived stolen session cannot plant its own passkey.
/// </para>
/// <para>
/// <b>The two sign-in actions are anonymous by necessity</b> (the caller is signing in) and carry
/// <see cref="WebApplicationBuilderExtensions.RateLimitPolicyAuthIp"/>, as login does; the framework's
/// anonymous-endpoint architecture gate lists them explicitly. <c>auth/passkeys/signin</c> is a default
/// token-issuing path of the same-origin proxy.
/// </para>
/// </remarks>
public abstract class PasskeysControllerBase : ApiControllerBase
{
    /// <summary>
    /// The caller's IP, recorded on the opened session. Reads the connection's remote address, which
    /// behind a proxy is the forwarded value only when the host has configured <c>UseForwardedHeaders</c>.
    /// </summary>
    protected string? ClientIpAddress => HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>The caller's user-agent, recorded on the opened session.</summary>
    protected string? ClientUserAgent => HttpContext?.Request.Headers.UserAgent.ToString();

    /// <summary>The ceremony service when passkeys are enabled on this host, otherwise <see langword="null"/>.</summary>
    private IPasskeyService? EnabledPasskeys =>
        HttpContext?.RequestServices.GetService<IPasskeyService>() is { IsEnabled: true } passkeys ? passkeys : null;

    private DateTimeOffset UtcNow =>
        (HttpContext?.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    /// <summary>Starts registering a passkey for the caller: returns the creation options and the flow to complete.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow and the WebAuthn creation options.</returns>
    [HttpPost("register/options")]
    [NonIdempotent("Each call stores a fresh single-use challenge. A replayed response would hand back a challenge the first completion may already have burned.")]
    [Authorize]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<PasskeyOptionsResponse>> BeginRegistrationAsync(CancellationToken cancellationToken)
    {
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!PasskeyStepUp.IsSatisfiedBy(User, UtcNow))
        {
            return HandleFailure([PasskeyErrors.StepUpRequired(nameof(BeginRegistrationAsync))]);
        }

        var (userName, displayName) = DescribeCaller(userId);
        var result = await passkeys.BeginRegistrationAsync(userId, userName, displayName, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// The names the authenticator shows for the account: the email (or the id) and the display name,
    /// read off the caller's token in either the mapped or the raw JWT claim form.
    /// </summary>
    /// <param name="userId">The caller's id, the last fallback.</param>
    /// <returns>The account name and the display name.</returns>
    private (string UserName, string DisplayName) DescribeCaller(UserIdentifierType userId)
    {
        var userName = User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? userId.ToString(CultureInfo.InvariantCulture);
        var displayName = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst("name")?.Value ?? userName;
        return (userName, displayName);
    }

    /// <summary>Completes a registration with the browser's attestation and stores the passkey.</summary>
    /// <param name="request">The flow, the attestation and the name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored passkey.</returns>
    [HttpPost("register")]
    [NonIdempotent("Completing burns the single-use challenge. A replay is refused by the challenge store, which is the correct answer; a cached response would hide that.")]
    [Authorize]
    [ProducesResponseType(typeof(PasskeyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<PasskeyResponse>> CompleteRegistrationAsync(
        [FromBody] PasskeyRegisterRequest request,
        CancellationToken cancellationToken)
    {
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (!PasskeyStepUp.IsSatisfiedBy(User, UtcNow))
        {
            return HandleFailure([PasskeyErrors.StepUpRequired(nameof(CompleteRegistrationAsync))]);
        }

        var result = await passkeys.CompleteRegistrationAsync(userId, request, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Lists the caller's passkeys, oldest first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The caller's passkeys.</returns>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<PasskeyResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<IReadOnlyList<PasskeyResponse>>> ListAsync(CancellationToken cancellationToken)
    {
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await passkeys.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Renames one of the caller's passkeys. Another account's passkey answers 404.</summary>
    /// <param name="id">The passkey row id.</param>
    /// <param name="request">The new name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The renamed passkey.</returns>
    [HttpPatch("{id:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(PasskeyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<PasskeyResponse>> RenameAsync(
        Guid id,
        [FromBody] PasskeyRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await passkeys.RenameAsync(userId, id, request.Name, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Deletes one of the caller's passkeys. The last one goes only while the account still has a
    /// password or an external login (409 <c>Passkeys.LastPasskey</c> otherwise); another account's
    /// passkey answers 404.
    /// </summary>
    /// <param name="id">The passkey row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content, or a Problem Details failure.</returns>
    [HttpDelete("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        if (User.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var result = await passkeys.DeleteAsync(userId, id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }

    /// <summary>Starts an anonymous passkey sign-in: returns the request options and the flow to complete.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow and the WebAuthn request options.</returns>
    [HttpPost("signin/options")]
    [NonIdempotent("Each call stores a fresh single-use challenge. A replayed response would hand back a challenge the first completion may already have burned.")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(typeof(PasskeyOptionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<PasskeyOptionsResponse>> BeginSignInAsync(CancellationToken cancellationToken)
    {
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        var result = await passkeys.BeginSignInAsync(cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Completes a passkey sign-in and returns the token pair, whose access token carries
    /// <c>mfa=passkey</c>.
    /// </summary>
    /// <param name="request">The flow and the assertion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token pair, or a Problem Details failure.</returns>
    [HttpPost("signin")]
    [NonIdempotent("Signing in issues a token pair and burns the single-use challenge. A replayed response would hand a retrying client tokens minted for an earlier call; a replay is refused instead, which is the correct answer.")]
    [AllowAnonymous]
    [EnableRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp)]
    [ProducesResponseType(typeof(AuthenticationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<AuthenticationResponse>> CompleteSignInAsync(
        [FromBody] PasskeySignInRequest request,
        CancellationToken cancellationToken)
    {
        if (EnabledPasskeys is not { } passkeys)
        {
            return NotFound();
        }

        var result = await passkeys
            .CompleteSignInAsync(request, ClientIpAddress, ClientUserAgent, cancellationToken)
            .ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }
}
