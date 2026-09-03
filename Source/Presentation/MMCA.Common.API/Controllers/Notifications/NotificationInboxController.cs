using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.API.Controllers.Notifications;

/// <summary>
/// REST controller for the user notification inbox.
/// Any authenticated user can access their own inbox.
/// </summary>
[ApiController]
[Route("Notifications/[controller]")]
[ApiVersion("1.0")]
[FeatureGate(NotificationFeatures.PushNotifications)]
[Authorize]
public sealed class InboxController(
    IQueryHandler<GetMyNotificationsQuery, Result<PagedCollectionResult<UserNotificationDTO>>> inboxHandler,
    IQueryHandler<GetUnreadNotificationCountQuery, Result<int>> unreadCountHandler,
    ICommandHandler<MarkNotificationReadCommand, Result> markReadHandler,
    ICommandHandler<MarkAllNotificationsReadCommand, Result> markAllReadHandler,
    ICurrentUserService currentUserService) : ApiControllerBase
{
    /// <summary>
    /// Gets the current user's notification inbox (GET /api/notifications/inbox).
    /// An optional <c>scope</c> narrows the inbox to the notifications carrying that scope plus the
    /// unscoped ones; omitting it returns everything, exactly as before.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedCollectionResult<UserNotificationDTO>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedCollectionResult<UserNotificationDTO>>> GetInboxAsync(
        [FromQuery, Range(1, int.MaxValue)] int pageNumber = 1,
        [FromQuery, Range(1, int.MaxValue)] int pageSize = 20,
        [FromQuery, StringLength(PushNotification.ScopeKeyMaxLength)] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("Notification.Unauthorized", "User is not authenticated.")]);
        }

        var query = new GetMyNotificationsQuery(userId.Value, pageNumber, pageSize, scope);
        Result<PagedCollectionResult<UserNotificationDTO>> result = await inboxHandler
            .HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Gets the count of unread notifications (GET /api/notifications/inbox/unread-count).
    /// The optional <c>scope</c> matches the inbox read, so the badge and the list agree.
    /// </summary>
    [HttpGet("unread-count")]
    [ResponseCache(NoStore = true)]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> GetUnreadCountAsync(
        [FromQuery, StringLength(PushNotification.ScopeKeyMaxLength)] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("Notification.Unauthorized", "User is not authenticated.")]);
        }

        var query = new GetUnreadNotificationCountQuery(userId.Value, scope);
        Result<int> result = await unreadCountHandler.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Marks a single notification as read (PUT /api/notifications/inbox/{id}/read).</summary>
    [HttpPut("{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public async Task<IActionResult> MarkReadAsync(
        [FromRoute] UserNotificationIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("Notification.Unauthorized", "User is not authenticated.")]);
        }

        var command = new MarkNotificationReadCommand(id, userId.Value);
        Result result = await markReadHandler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }

    /// <summary>
    /// Marks all notifications as read (PUT /api/notifications/inbox/read-all).
    /// The optional <c>scope</c> keeps the write aligned with what the caller can see: a scoped
    /// caller never clears notifications its inbox read hid from it.
    /// </summary>
    [HttpPut("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllReadAsync(
        [FromQuery, StringLength(PushNotification.ScopeKeyMaxLength)] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        UserIdentifierType? userId = currentUserService.UserId;
        if (!userId.HasValue)
        {
            return HandleFailure([Error.Unauthorized("Notification.Unauthorized", "User is not authenticated.")]);
        }

        var command = new MarkAllNotificationsReadCommand(userId.Value, scope);
        Result result = await markAllReadHandler.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }
}
