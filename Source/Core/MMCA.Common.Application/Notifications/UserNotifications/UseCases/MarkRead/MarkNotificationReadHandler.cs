using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;

/// <summary>
/// Handles marking a single notification as read. Verifies that the notification
/// belongs to the requesting user before updating.
/// </summary>
public sealed class MarkNotificationReadHandler(
    IUnitOfWork unitOfWork,
    IQueryableExecutor queryableExecutor) : ICommandHandler<MarkNotificationReadCommand, Result>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        MarkNotificationReadCommand command,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();

        List<UserNotification> matches = await queryableExecutor.ToListAsync(
            repository.Table
                .Where(un => un.Id == command.NotificationId && un.UserId == command.UserId)
                .Take(1),
            cancellationToken).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return Result.Failure(Error.NotFoundError(
                code: "UserNotification.NotFound",
                message: "Notification not found.",
                source: nameof(MarkNotificationReadHandler)));
        }

        matches[0].MarkAsRead();
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
