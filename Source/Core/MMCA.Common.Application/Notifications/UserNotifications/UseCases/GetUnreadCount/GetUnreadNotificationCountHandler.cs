using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
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

        int count = await queryableExecutor.CountAsync(
            repository.TableNoTracking.Where(un => un.UserId == query.UserId && !un.IsRead),
            cancellationToken).ConfigureAwait(false);

        return Result.Success(count);
    }
}
