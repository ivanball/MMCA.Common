using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;

/// <summary>
/// Handles sending a push notification to all recipients. Creates a <see cref="PushNotification"/>
/// entity for audit, queries recipient user IDs via <see cref="INotificationRecipientProvider"/>, and
/// dispatches via <see cref="IPushNotificationSender"/>.
/// </summary>
public sealed partial class SendPushNotificationHandler(
    IUnitOfWork unitOfWork,
    INotificationRecipientProvider recipientProvider,
    IPushNotificationSender pushNotificationSender,
    PushNotificationDTOMapper dtoMapper,
    ILogger<SendPushNotificationHandler> logger) : ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>>
{
    /// <inheritdoc />
    public async Task<Result<PushNotificationDTO>> HandleAsync(
        SendPushNotificationCommand command,
        CancellationToken cancellationToken = default)
    {
        // Query all recipient user IDs via the app-specific provider
        IReadOnlyList<UserIdentifierType> recipientIds = await recipientProvider
            .GetRecipientUserIdsAsync(cancellationToken);

        if (recipientIds.Count == 0)
        {
            return Result.Failure<PushNotificationDTO>(Error.Validation(
                code: "PushNotification.NoRecipients",
                message: "There are no recipients to send the notification to.",
                source: nameof(SendPushNotificationHandler)));
        }

        // Create the notification entity
        Result<PushNotification> createResult = PushNotification.Create(
            command.Request.Title,
            command.Request.Body,
            command.SentByUserId,
            recipientIds.Count);
        if (createResult.IsFailure)
        {
            return Result.Failure<PushNotificationDTO>(createResult.Errors);
        }

        PushNotification notification = createResult.Value!;
        var repository = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();
        await repository.AddAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Create per-user inbox records so recipients can retrieve missed notifications
        var userNotificationRepo = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();
        foreach (var recipientId in recipientIds)
        {
            var userNotification = UserNotification.Create(recipientId, notification.Id);
            await userNotificationRepo.AddAsync(userNotification, cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Send the notification via SignalR (or other configured sender)
        try
        {
            await pushNotificationSender.SendToUsersAsync(
                recipientIds,
                command.Request.Title,
                command.Request.Body,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            notification.MarkAsSent();
            LogNotificationSent(logger, notification.Id, recipientIds.Count);
        }
#pragma warning disable CA1031 // Do not catch general exception types — delivery failure is non-fatal; we record the status
        catch (Exception ex)
#pragma warning restore CA1031
        {
            notification.MarkAsFailed();
            LogNotificationFailed(logger, notification.Id, ex);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(dtoMapper.MapToDTO(notification));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push notification {NotificationId} sent to {RecipientCount} recipients")]
    private static partial void LogNotificationSent(ILogger logger, PushNotificationIdentifierType notificationId, int recipientCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Push notification {NotificationId} delivery failed")]
    private static partial void LogNotificationFailed(ILogger logger, PushNotificationIdentifierType notificationId, Exception exception);
}
