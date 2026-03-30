using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.API.Controllers.Notifications;

/// <summary>
/// REST controller for push notification operations.
/// Only organizers can send notifications and view notification history.
/// </summary>
[ApiController]
[Route("[controller]")]
[ApiVersion("1.0")]
[FeatureGate(NotificationFeatures.PushNotifications)]
[Authorize(Policy = AuthorizationPolicies.RequireOrganizer)]
public sealed class NotificationsController(
    ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>> sendHandler,
    IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>> historyHandler,
    ICurrentUserService currentUserService) : ApiControllerBase
{
    /// <summary>Sends a push notification to all recipients (POST /api/notifications).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PushNotificationDTO), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    public async Task<ActionResult<PushNotificationDTO>> SendAsync(
        [FromBody] SendPushNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("Notification.Unauthorized", "User is not authenticated.")]);
        }

        var command = new SendPushNotificationCommand(request, userId.Value);
        Result<PushNotificationDTO> result = await sendHandler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Created(new Uri($"/notifications/{result.Value!.Id}", UriKind.Relative), result.Value);
    }

    /// <summary>Gets push notification history (GET /api/notifications).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedCollectionResult<PushNotificationDTO>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedCollectionResult<PushNotificationDTO>>> GetHistoryAsync(
        [FromQuery, Range(1, int.MaxValue)] int pageNumber = 1,
        [FromQuery, Range(1, int.MaxValue)] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var query = new GetNotificationHistoryQuery(pageNumber, pageSize);
        Result<PagedCollectionResult<PushNotificationDTO>> result = await historyHandler
            .HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Ok(result.Value);
    }
}
