using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Shared.Auth.MachineClients;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.API.Controllers.MachineClients;

/// <summary>
/// Base controller for the machine-client administration endpoints (ADR-140): list, create (the secret
/// is shown once), rotate, promote and revoke.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and the API version, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Admin/MachineClients")]
/// [ApiVersion("1.0")]
/// public sealed class AdminMachineClientsController : MachineClientsControllerBase;
/// </code>
/// <para>
/// <b>Off means no routes.</b> Every action answers 404 unless the host called
/// <c>AddMachineClients</c> AND set <c>MachineClients:Enabled</c>; the service is resolved per action
/// for that reason, since a host without <c>AddMachineClients</c> has none to inject.
/// </para>
/// <para>
/// <b>Refusals a client has to expect:</b> <c>MachineClients.RoleNotAllowed</c> (400) for a role that
/// grants <c>users:view-as</c> or <c>users:manage</c>, <c>MachineClients.NotFound</c> (404),
/// <c>MachineClients.Revoked</c> and <c>MachineClients.NoSecondarySecret</c> (409).
/// </para>
/// </remarks>
[HasPermission(AdministrationPermissions.ManageClients)]
public abstract class MachineClientsControllerBase : ApiControllerBase
{
    /// <summary>The machine-client service when the feature is enabled on this host, otherwise <see langword="null"/>.</summary>
    private IMachineClientService? EnabledMachineClients =>
        HttpContext?.RequestServices.GetService<IMachineClientService>() is { IsEnabled: true } machineClients ? machineClients : null;

    /// <summary>Lists every machine client, revoked ones included.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The clients.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<MachineClientResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<IReadOnlyList<MachineClientResponse>>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (EnabledMachineClients is not { } machineClients)
        {
            return NotFound();
        }

        var result = await machineClients.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Creates a machine client and its service account; the response carries the secret, once.</summary>
    /// <param name="request">The name, role and optional secret expiry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client and its secret.</returns>
    [HttpPost]
    [NonIdempotent("The response carries the client's only copy of its secret: replaying it would keep the secret in the replay cache, and a retry that creates a second client is visible and revocable.")]
    [ProducesResponseType(typeof(MachineClientSecretResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<MachineClientSecretResponse>> CreateAsync(
        [FromBody] CreateMachineClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EnabledMachineClients is not { } machineClients)
        {
            return NotFound();
        }

        var result = await machineClients.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Issues a secondary secret, returned once; both secrets work until it is promoted.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client and the new secret.</returns>
    [HttpPost("{id:guid}/rotate")]
    [NonIdempotent("The response carries the only copy of the new secret, which must not rest in the replay cache; a second rotation simply replaces the waiting secret.")]
    [ProducesResponseType(typeof(MachineClientSecretResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<MachineClientSecretResponse>> RotateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledMachineClients is not { } machineClients)
        {
            return NotFound();
        }

        var result = await machineClients.RotateAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Makes the secondary secret the primary one; the old primary stops working.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client.</returns>
    [HttpPost("{id:guid}/promote")]
    [Idempotent]
    [ProducesResponseType(typeof(MachineClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<MachineClientResponse>> PromoteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledMachineClients is not { } machineClients)
        {
            return NotFound();
        }

        var result = await machineClients.PromoteAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Revokes a client: its secrets stop working and every service rejects the tokens it holds. Revoking
    /// a revoked client succeeds.
    /// </summary>
    /// <param name="id">The client id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client.</returns>
    [HttpPost("{id:guid}/revoke")]
    [Idempotent]
    [ProducesResponseType(typeof(MachineClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<MachineClientResponse>> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledMachineClients is not { } machineClients)
        {
            return NotFound();
        }

        var result = await machineClients.RevokeAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }
}
