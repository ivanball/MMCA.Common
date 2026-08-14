using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
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
    /// <summary>Ceiling on the inbox page size, matching the documented "max 500" on the query.</summary>
    private const int MaxPageSize = 500;

    /// <inheritdoc />
    public async Task<Result<PagedCollectionResult<UserNotificationDTO>>> HandleAsync(
        GetMyNotificationsQuery query,
        CancellationToken cancellationToken = default)
    {
        // Shared 64-bit clamp: Skip/Take must not be derived from a 32-bit
        // (PageNumber - 1) * PageSize, which wraps negative near int.MaxValue and makes SQL Server
        // reject the negative OFFSET. It also floors the page size, which the [Range] attribute at
        // the API boundary enforces but a direct handler caller does not inherit.
        var (skip, take) = PagingMath.Clamp(query.PageNumber, query.PageSize, MaxPageSize);
        var userNotificationRepo = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();
        var pushNotificationRepo = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();

        // Scope is a view filter, not a security boundary: a read that supplies one sees the
        // notifications carrying that scope plus the unscoped ones, and a read that supplies none
        // keeps the legacy query untouched (the join stays over the whole table).
        IQueryable<PushNotification> pushNotifications = pushNotificationRepo.TableNoTracking;
        if (!string.IsNullOrWhiteSpace(query.ScopeKey))
        {
            string scopeKey = query.ScopeKey;
            pushNotifications = pushNotifications.Where(pn => pn.ScopeKey == null || pn.ScopeKey == scopeKey);
        }

        var joined = from un in userNotificationRepo.TableNoTracking
                     join pn in pushNotifications on un.PushNotificationId equals pn.Id
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
            joined.Skip(skip).Take(take),
            cancellationToken).ConfigureAwait(false);

        // The floor mirrors what PagingMath.Clamp already applied for the read, so the metadata
        // reports the page actually served instead of throwing on a sub-1 page number.
        var metadata = new PaginationMetadata(totalCount, take, Math.Max(query.PageNumber, 1));
        return Result.Success(new PagedCollectionResult<UserNotificationDTO>(paged, metadata));
    }
}
