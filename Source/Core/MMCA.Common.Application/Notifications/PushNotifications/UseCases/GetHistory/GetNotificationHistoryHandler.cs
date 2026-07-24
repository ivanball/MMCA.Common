using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
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
    /// <summary>Ceiling on the history page size, matching the documented "max 500" on the query.</summary>
    private const int MaxPageSize = 500;

    /// <inheritdoc />
    public async Task<Result<PagedCollectionResult<PushNotificationDTO>>> HandleAsync(
        GetNotificationHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        // Shared 64-bit clamp: see PagingMath. A 32-bit (PageNumber - 1) * PageSize wraps negative
        // near int.MaxValue, and SQL Server rejects a negative OFFSET outright.
        var (skip, take) = PagingMath.Clamp(query.PageNumber, query.PageSize, MaxPageSize);
        var repository = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();

        int totalCount = await repository.CountAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<PushNotification> paged = await queryableExecutor.ToListAsync(
            repository.TableNoTracking
                .OrderByDescending(n => n.CreatedOn)
                .Skip(skip)
                .Take(take),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyCollection<PushNotificationDTO> dtos = dtoMapper.MapToDTOs(paged);
        var metadata = new PaginationMetadata(totalCount, take, query.PageNumber);

        return Result.Success(new PagedCollectionResult<PushNotificationDTO>(dtos, metadata));
    }
}
