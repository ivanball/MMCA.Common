using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;

/// <summary>
/// Handles retrieving the count of unread notifications for a user.
/// Used by the notification bell badge in the UI.
/// </summary>
public sealed class GetUnreadNotificationCountHandler(
    IUnitOfWork unitOfWork,
    IQueryableExecutor queryableExecutor) : IQueryHandler<GetUnreadNotificationCountQuery, Result<int>>
{
    /// <inheritdoc />
    public async Task<Result<int>> HandleAsync(
        GetUnreadNotificationCountQuery query,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();

        IQueryable<UserNotification> unread = repository.TableNoTracking
            .Where(un => un.UserId == query.UserId && !un.IsRead);

        // The join to PushNotification is introduced ONLY for a scoped count. An unconditional join
        // would drag PushNotification's soft-delete global query filter into the legacy no-scope
        // count and change a number that no caller asked to change; a scoped count deliberately
        // accepts that narrowing, since a scoped reader cannot see a deleted parent anyway.
        if (!string.IsNullOrWhiteSpace(query.ScopeKey))
        {
            string scopeKey = query.ScopeKey;
            var pushNotificationRepo = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();

            unread = from un in unread
                     join pn in pushNotificationRepo.TableNoTracking on un.PushNotificationId equals pn.Id
                     where pn.ScopeKey == null || pn.ScopeKey == scopeKey
                     select un;
        }

        int count = await queryableExecutor.CountAsync(unread, cancellationToken).ConfigureAwait(false);

        return Result.Success(count);
    }
}
