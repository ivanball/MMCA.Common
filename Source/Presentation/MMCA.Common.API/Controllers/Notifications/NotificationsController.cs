using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.API.Controllers.Notifications;

/// <summary>
/// REST controller for push notification operations. Sending notifications and reading the send
/// history both require the <see cref="NotificationPermissions.Manage"/> capability, which a host
/// grants to whichever roles it wants to hold it.
/// </summary>
[ApiController]
[Route("[controller]")]
[ApiVersion("1.0")]
[FeatureGate(NotificationFeatures.PushNotifications)]
[HasPermission(NotificationPermissions.Manage)]
public sealed class NotificationsController(
    ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>> sendHandler,
    IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>> historyHandler,
    ICurrentUserService currentUserService) : ApiControllerBase
{
    /// <summary>
    /// Sends a push notification to all recipients (POST /api/notifications).
    /// Retry-safe on two levels that work together: <see cref="IdempotentAttribute"/> replays the
    /// original HTTP response for a repeated <c>Idempotency-Key</c>, and the same key is passed on
    /// as the command's <c>DedupKey</c> so the domain refuses a second send even when the filter's
    /// cache is cold, evicted, or degraded (a restarted host, an expired 24h entry, an unreachable
    /// distributed cache). The filter alone protects the response; the DedupKey protects delivery.
    /// </summary>
    [HttpPost]
    [Idempotent]
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

        // Carry the client's idempotency key into the domain. Absent or whitespace-only stays null,
        // which is exactly the legacy behaviour (every send creates a new notification).
        string? dedupKey = null;
#pragma warning disable S6932 // Idempotency-Key is protocol plumbing shared with IdempotencyFilter, not an action parameter: binding it with [FromHeader] would add it to the generated OpenAPI contract.
        if (Request.Headers.TryGetValue(IdempotencyHeaders.IdempotencyKey, out var keyValues))
        {
            string headerValue = keyValues.ToString();
            dedupKey = string.IsNullOrWhiteSpace(headerValue) ? null : headerValue;
        }
#pragma warning restore S6932

        var command = new SendPushNotificationCommand(request, userId.Value) { DedupKey = dedupKey };
        Result<PushNotificationDTO> result = await sendHandler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : Created(new Uri(string.Create(CultureInfo.InvariantCulture, $"/notifications/{result.Value!.Id}"), UriKind.Relative), result.Value);
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
