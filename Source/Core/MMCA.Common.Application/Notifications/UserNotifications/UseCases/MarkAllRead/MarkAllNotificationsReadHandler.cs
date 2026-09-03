using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;

/// <summary>
/// Handles marking all of a user's unread notifications as read.
/// </summary>
public sealed class MarkAllNotificationsReadHandler(
    IUnitOfWork unitOfWork,
    IQueryableExecutor queryableExecutor,
    TimeProvider timeProvider) : ICommandHandler<MarkAllNotificationsReadCommand, Result>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        MarkAllNotificationsReadCommand command,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();

        IQueryable<UserNotification> unreadQuery = repository.Table
            .Where(un => un.UserId == command.UserId && !un.IsRead);

        // Same conditional join as the unread count, for two reasons: a scoped client must not mark
        // rows it cannot see as read, and a no-scope command must keep the legacy query exactly as
        // it was rather than inherit PushNotification's soft-delete global query filter.
        if (!string.IsNullOrWhiteSpace(command.ScopeKey))
        {
            string scopeKey = command.ScopeKey;
            var pushNotificationRepo = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();

            // The TRACKED Table is load-bearing here, unlike the read-only handlers that join over
            // TableNoTracking: an AsNoTracking source anywhere in a composed EF query switches the
            // WHOLE query to no-tracking, so the UserNotification rows would come back untracked and
            // the MarkAsRead mutations below would never be persisted by SaveChangesAsync (a scoped
            // read-all would silently no-op). Projecting `select un` materializes only
            // UserNotification instances, so no PushNotification is tracked by this join.
            unreadQuery = from un in unreadQuery
                          join pn in pushNotificationRepo.Table on un.PushNotificationId equals pn.Id
                          where pn.ScopeKey == null || pn.ScopeKey == scopeKey
                          select un;
        }

        List<UserNotification> unread = await queryableExecutor.ToListAsync(
            unreadQuery,
            cancellationToken).ConfigureAwait(false);

        var readOnUtc = timeProvider.GetUtcNow().UtcDateTime;
        foreach (UserNotification notification in unread)
        {
            notification.MarkAsRead(readOnUtc);
        }

        if (unread.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
