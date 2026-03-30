using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;

/// <summary>
/// Handles retrieving a user's notification inbox with pagination.
/// Joins <see cref="UserNotification"/> with <see cref="PushNotification"/> to
/// project title, body, and read status into a single DTO.
/// </summary>
public sealed class GetMyNotificationsHandler(
    IUnitOfWork unitOfWork,
    IQueryableExecutor queryableExecutor) : IQueryHandler<GetMyNotificationsQuery, Result<PagedCollectionResult<UserNotificationDTO>>>
{
    /// <inheritdoc />
    public async Task<Result<PagedCollectionResult<UserNotificationDTO>>> HandleAsync(
        GetMyNotificationsQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Min(query.PageSize, 500);
        var userNotificationRepo = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();
        var pushNotificationRepo = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();

        var joined = from un in userNotificationRepo.TableNoTracking
                     join pn in pushNotificationRepo.TableNoTracking on un.PushNotificationId equals pn.Id
                     where un.UserId == query.UserId
                     orderby pn.CreatedOn descending
                     select new UserNotificationDTO
                     {
                         Id = un.Id,
                         PushNotificationId = pn.Id,
                         Title = pn.Title,
                         Body = pn.Body,
                         IsRead = un.IsRead,
                         ReadOn = un.ReadOn,
                         SentOn = pn.CreatedOn,
                     };

        int totalCount = await queryableExecutor.CountAsync(joined, cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<UserNotificationDTO> paged = await queryableExecutor.ToListAsync(
            joined.Skip((query.PageNumber - 1) * pageSize).Take(pageSize),
            cancellationToken).ConfigureAwait(false);

        var metadata = new PaginationMetadata(totalCount, pageSize, query.PageNumber);
        return Result.Success(new PagedCollectionResult<UserNotificationDTO>(paged, metadata));
    }
}
