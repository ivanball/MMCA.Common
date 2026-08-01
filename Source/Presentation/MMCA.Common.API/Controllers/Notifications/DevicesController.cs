using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.API.Controllers.Notifications;

/// <summary>
/// REST controller for native push device installations (ADR-044). Any authenticated user
/// manages THEIR device registrations: PUT upserts the installation (called after login and on
/// token rotation), DELETE removes it (called before logout). Installation ids are
/// client-generated GUIDs, so they are not enumerable; ownership is stamped server-side from
/// the authenticated user.
/// </summary>
[ApiController]
[Route("Notifications/Devices")]
[ApiVersion("1.0")]
[FeatureGate(NotificationFeatures.PushNotifications)]
[Authorize]
public sealed class DevicesController(
    IPushDeviceRegistrar registrar,
    ICurrentUserService currentUserService) : ApiControllerBase
{
    /// <summary>Registers or refreshes this device's push installation (PUT /Notifications/Devices).</summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    public async Task<ActionResult> UpsertAsync(
        [FromBody] DeviceInstallationRequest request,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("PushDevice.Unauthorized", "User is not authenticated.")]);
        }

        Result result = await registrar.UpsertAsync(userId.Value, request, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }

    /// <summary>
    /// Removes a device installation (DELETE /Notifications/Devices/{installationId}). Idempotent,
    /// and scoped to the caller: the id comes from the client, so the registrar verifies the
    /// installation's owner tag before deleting. An id that does not exist and an id belonging to
    /// another user both answer 204 without deleting anything, so the response cannot be used to
    /// probe for other users' installation ids.
    /// </summary>
    [HttpDelete("{installationId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    public async Task<ActionResult> DeleteAsync(
        string installationId,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("PushDevice.Unauthorized", "User is not authenticated.")]);
        }

        Result result = await registrar.DeleteAsync(userId.Value, installationId, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }
}
