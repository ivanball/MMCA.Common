using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;

/// <summary>
/// Handles retrieving push notification history with pagination.
/// Returns notifications in reverse chronological order.
/// </summary>
public sealed class GetNotificationHistoryHandler(
    IUnitOfWork unitOfWork,
    IQueryableExecutor queryableExecutor,
    PushNotificationDTOMapper dtoMapper) : IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>>
{
    /// <inheritdoc />
    public async Task<Result<PagedCollectionResult<PushNotificationDTO>>> HandleAsync(
        GetNotificationHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Min(query.PageSize, 500);
        var repository = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();

        int totalCount = await repository.CountAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<PushNotification> paged = await queryableExecutor.ToListAsync(
            repository.TableNoTracking
                .OrderByDescending(n => n.CreatedOn)
                .Skip((query.PageNumber - 1) * pageSize)
                .Take(pageSize),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<PushNotificationDTO> dtos = dtoMapper.MapToDTOs(paged);
        var metadata = new PaginationMetadata(totalCount, pageSize, query.PageNumber);

        return Result.Success(new PagedCollectionResult<PushNotificationDTO>(dtos, metadata));
    }
}
