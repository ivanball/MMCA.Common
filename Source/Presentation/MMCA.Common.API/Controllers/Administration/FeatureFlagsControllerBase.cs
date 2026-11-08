using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.API.Controllers.Administration;

/// <summary>
/// Base controller for the opt-in feature-flag administration endpoints (ADR-031): list the flags this
/// service declares, and set or clear their stored overrides.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and the API version, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Admin/FeatureFlags")]
/// [ApiVersion("1.0")]
/// public sealed class AdminFeatureFlagsController(IFeatureFlagAdministrationService service)
///     : FeatureFlagsControllerBase(service);
/// </code>
/// <para>
/// <c>AddReloadableFeatureFlags()</c> registers the service. Each service of a multi-service host mounts
/// its own derived controller, because each owns its overrides in its own database; the shared UI page
/// lists them together from a configured list of route prefixes.
/// </para>
/// <para>
/// <b>Refusals a client has to expect:</b> <c>FeatureFlag.Undeclared</c> (404) for a name no
/// <c>*Features</c> class declares, and the <c>FeatureFlagOverride.*</c> validation codes (400), of
/// which <c>FeatureFlagOverride.ReasonRequired</c> is the one a form most often meets.
/// </para>
/// </remarks>
/// <param name="administration">The feature-flag administration service.</param>
[HasPermission(AdministrationPermissions.ManageFeatures)]
public abstract class FeatureFlagsControllerBase(IFeatureFlagAdministrationService administration) : ApiControllerBase
{
    /// <summary>Gets the feature-flag administration service (exposed for app-level overrides).</summary>
    protected IFeatureFlagAdministrationService Administration { get; } = administration;

    /// <summary>
    /// Lists every declared flag with its kind, owner, configured value, stored override and
    /// effective value.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flags, or a Problem Details failure.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<FeatureFlagStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<IReadOnlyList<FeatureFlagStatusResponse>>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await Administration.ListAsync(cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Sets the stored override of one declared flag and returns the flag's state afterwards.
    /// </summary>
    /// <param name="name">The flag name.</param>
    /// <param name="request">The override, with its required reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flag's state, or a Problem Details failure.</returns>
    [HttpPut("{name}")]
    [ProducesResponseType(typeof(FeatureFlagStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<FeatureFlagStatusResponse>> SetOverrideAsync(
        string name,
        [FromBody] SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await Administration.SetOverrideAsync(name, request, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Clears the stored override of one declared flag, so configuration is in force again. Clearing a
    /// flag with no override succeeds.
    /// </summary>
    /// <param name="name">The flag name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content, or a Problem Details failure.</returns>
    [HttpDelete("{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> ClearOverrideAsync(string name, CancellationToken cancellationToken = default)
    {
        var result = await Administration.ClearOverrideAsync(name, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }
}
