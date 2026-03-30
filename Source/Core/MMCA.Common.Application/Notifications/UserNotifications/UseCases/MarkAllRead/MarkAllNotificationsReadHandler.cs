using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;

/// <summary>
/// Handles marking all of a user's unread notifications as read.
/// </summary>
public sealed class MarkAllNotificationsReadHandler(
    IUnitOfWork unitOfWork,
    IQueryableExecutor queryableExecutor) : ICommandHandler<MarkAllNotificationsReadCommand, Result>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        MarkAllNotificationsReadCommand command,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();

        List<UserNotification> unread = await queryableExecutor.ToListAsync(
            repository.Table.Where(un => un.UserId == command.UserId && !un.IsRead),
            cancellationToken).ConfigureAwait(false);

        foreach (UserNotification notification in unread)
        {
            notification.MarkAsRead();
        }

        if (unread.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
