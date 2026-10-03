using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.API.Controllers.Legal;

/// <summary>
/// Base controller for the signed-in user's Terms of Service acceptance: <c>GET</c> reads the
/// caller's standing against the configured current version, <c>POST</c> records an acceptance.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and nothing else (the <c>DataExportControllerBase</c>
/// precedent): routed at <c>Users</c>, it serves <c>/Users/me/legal-acceptance</c>, the path the UI
/// client calls (<see cref="LegalAcceptanceRoutes.Path"/>).
/// </para>
/// <code>
/// [ApiController]
/// [Route("Users")]
/// [ApiVersion("1.0")]
/// public sealed class UsersLegalAcceptanceController(
///     ILegalAcceptanceService service,
///     ICurrentUserService currentUserService,
///     IOptions&lt;LegalAcceptanceOptions&gt; options)
///     : LegalAcceptanceControllerBase(service, currentUserService, options);
/// </code>
/// <para>
/// The version rules live here, not in the consumer's service: an acceptance is refused unless it
/// names the configured current version (<see cref="LegalAcceptancePolicy.EnsureAcceptsCurrentVersion"/>),
/// and every answer is re-derived against that version (<see cref="LegalAcceptancePolicy.Normalize"/>).
/// With no version configured, <c>GET</c> answers <c>IsCurrent = true</c> without calling the
/// service, so a client never blocks a user on a host that has not opted in.
/// </para>
/// </remarks>
/// <param name="legalAcceptanceService">The app's acceptance store.</param>
/// <param name="currentUserService">Identifies the caller.</param>
/// <param name="options">The configured current terms version.</param>
[Authorize]
public abstract class LegalAcceptanceControllerBase(
    ILegalAcceptanceService legalAcceptanceService,
    ICurrentUserService currentUserService,
    IOptions<LegalAcceptanceOptions> options) : ApiControllerBase
{
    /// <summary>The current user service for this controller.</summary>
    protected ICurrentUserService CurrentUserService { get; } = currentUserService;

    /// <summary>The configured current terms version, or <see langword="null"/> when none is configured.</summary>
    protected string? CurrentTermsVersion => LegalAcceptancePolicy.ResolveCurrentVersion(options.Value);

    /// <summary>Reads the signed-in user's Terms of Service standing.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The standing, or a Problem Details failure.</returns>
    [HttpGet(LegalAcceptanceRoutes.Action)]
    [ProducesResponseType(typeof(LegalAcceptanceDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<IActionResult> GetLegalAcceptanceAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = CurrentTermsVersion;
        if (currentVersion is null)
        {
            return Ok(LegalAcceptanceDTO.Evaluate(null, null, null));
        }

        var currentUserId = CurrentUserService.UserId;
        if (currentUserId is null)
        {
            return HandleFailure([Error.Unauthorized("Legal.Unauthorized", "User is not authenticated.")]);
        }

        var result = await legalAcceptanceService
            .GetForCurrentUserAsync(currentUserId.Value, currentVersion, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(LegalAcceptancePolicy.Normalize(currentVersion, result.Value!));
    }

    /// <summary>
    /// Records that the signed-in user accepted the current Terms of Service. The request must name
    /// the configured current version.
    /// </summary>
    /// <param name="request">The version the user agreed to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The standing after the acceptance, or a Problem Details failure.</returns>
    [HttpPost(LegalAcceptanceRoutes.Action)]
    [ProducesResponseType(typeof(LegalAcceptanceDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<IActionResult> AcceptLegalTermsAsync(
        [FromBody] AcceptLegalTermsRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = CurrentUserService.UserId;
        if (currentUserId is null)
        {
            return HandleFailure([Error.Unauthorized("Legal.Unauthorized", "User is not authenticated.")]);
        }

        var currentVersion = CurrentTermsVersion;
        var versionCheck = LegalAcceptancePolicy.EnsureAcceptsCurrentVersion(
            currentVersion, request.Version, nameof(AcceptLegalTermsAsync));
        if (versionCheck.IsFailure)
        {
            return HandleFailure(versionCheck.Errors);
        }

        // The configured value, never the client's string: the check above proved them equal, and
        // this keeps the stored version byte-identical to configuration.
        var result = await legalAcceptanceService
            .AcceptForCurrentUserAsync(currentUserId.Value, currentVersion!, cancellationToken)
            .ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(LegalAcceptancePolicy.Normalize(currentVersion, result.Value!));
    }
}
