using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;

/// <summary>
/// Handles marking all of a user's unread notifications as read.
/// </summary>
/// <remarks>
/// One set-based UPDATE rather than loading and tracking every unread row: an inbox can hold an
/// unbounded number of them, and <see cref="UserNotification.MarkAsRead"/> only sets two columns and
/// raises no domain event, so nothing is lost by bypassing the change tracker.
/// </remarks>
public sealed class MarkAllNotificationsReadHandler(
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<MarkAllNotificationsReadCommand, Result>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        MarkAllNotificationsReadCommand command,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();

        Expression<Func<UserNotification, bool>> unread =
            un => un.UserId == command.UserId && !un.IsRead;

        // Same condition as the unread count, for two reasons: a scoped client must not mark rows it
        // cannot see as read, and a no-scope command must keep the legacy predicate exactly as it was
        // rather than inherit PushNotification's soft-delete global query filter. The scope test is
        // an EXISTS subquery over the push notification set, to which that filter applies exactly as
        // it did to the former join.
        if (!string.IsNullOrWhiteSpace(command.ScopeKey))
        {
            string scopeKey = command.ScopeKey;
            var pushNotifications = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>().Table;

            unread = un => un.UserId == command.UserId
                && !un.IsRead
                && pushNotifications.Any(pn => pn.Id == un.PushNotificationId
                    && (pn.ScopeKey == null || pn.ScopeKey == scopeKey));
        }

        var readOnUtc = timeProvider.GetUtcNow().UtcDateTime;

        await repository.ExecuteUpdateAsync(
            unread,
            setters => setters
                .Set(un => un.IsRead, true)
                .Set(un => un.ReadOn, (DateTime?)readOnUtc),
            cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
