using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;

/// <summary>
/// Handles sending a push notification to all recipients. Creates a <see cref="PushNotification"/>
/// entity for audit, queries recipient user IDs via <see cref="INotificationRecipientProvider"/>, and
/// dispatches via <see cref="IPushNotificationSender"/>.
/// <para>
/// <b>Atomicity.</b> The three saves below (audit row, recipient rows, terminal status) are one unit:
/// <see cref="SendPushNotificationCommand"/> is <c>ITransactional</c>, so a fault anywhere in the
/// sequence rolls the audit row back with everything else. That is what keeps the dedup
/// short-circuit honest, since a committed row is otherwise indistinguishable from a delivered one
/// and would answer every retry of that key with success. A business failure (no recipients) returns
/// before the first write, and a delivery that fails is still recorded, because
/// <c>MarkAsFailed</c> ends in a success result. The live legs (SignalR and native) run inside the
/// unit, so a transient fault on the final status save re-runs them under the execution strategy:
/// delivery is at-least-once for the live channels and exactly-once for the inbox rows.
/// </para>
/// <para>
/// <b>Deduplication race.</b> The dedup lookup is a check-then-act: two concurrent sends of one key
/// can both pass it, and the loser fails its first save on the filtered unique index on
/// <c>DedupKey</c>. That failure propagates as the persistence exception (a 409 through
/// <c>DbUpdateExceptionHandler</c>) and the transactional decorator rolls the attempt back; the
/// client's retry is then answered by the dedup lookup with the winner's notification. Recovering
/// in place is not possible here: the failed insert stays tracked (nothing in the Application
/// contract can detach it), so the commit would throw, and on PostgreSQL the unique violation has
/// already aborted the transaction a requery would run in.
/// </para>
/// </summary>
public sealed partial class SendPushNotificationHandler(
    IUnitOfWork unitOfWork,
    INotificationRecipientProvider recipientProvider,
    IPushNotificationSender pushNotificationSender,
    INativePushSender nativePushSender,
    PushNotificationDTOMapper dtoMapper,
    ILogger<SendPushNotificationHandler> logger) : ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>>
{
    /// <inheritdoc />
    public async Task<Result<PushNotificationDTO>> HandleAsync(
        SendPushNotificationCommand command,
        CancellationToken cancellationToken = default)
    {
        // Deduplication (opt-in): a retried send carrying the same key must not deliver twice.
        // Whitespace is treated as absent so a blank header cannot claim the single "empty" key. The
        // persisted key is scoped to the sender, so another caller reusing the same client key can
        // neither suppress this send nor be handed this sender's notification.
        string? dedupKey = string.IsNullOrWhiteSpace(command.DedupKey)
            ? null
            : SenderScopedDedupKey(command.SentByUserId, command.DedupKey);
        if (dedupKey is not null)
        {
            PushNotification? alreadySent = await FindByDedupKeyAsync(dedupKey, cancellationToken).ConfigureAwait(false);
            if (alreadySent is not null)
            {
                LogDedupHit(logger, alreadySent.Id, dedupKey);
                return Result.Success(dtoMapper.MapToDTO(alreadySent));
            }
        }

        // Query all recipient user IDs via the app-specific provider
        IReadOnlyList<UserIdentifierType> recipientIds = await recipientProvider
            .GetRecipientUserIdsAsync(cancellationToken).ConfigureAwait(false);

        if (recipientIds.Count == 0)
        {
            return Result.Failure<PushNotificationDTO>(Error.Validation(
                code: "PushNotification.NoRecipients",
                message: "There are no recipients to send the notification to.",
                source: nameof(SendPushNotificationHandler)));
        }

        // Create the notification entity
        Result<PushNotification> createResult = PushNotification.Create(
            command.Request.Title,
            command.Request.Body,
            command.SentByUserId,
            recipientIds.Count,
            dedupKey,
            command.Request.ScopeKey);
        if (createResult.IsFailure)
        {
            return Result.Failure<PushNotificationDTO>(createResult.Errors);
        }

        PushNotification notification = createResult.Value!;
        var repository = unitOfWork.GetRepository<PushNotification, PushNotificationIdentifierType>();
        await repository.AddAsync(notification, cancellationToken).ConfigureAwait(false);

        // A lost race on the DedupKey unique index fails here and propagates (see the class remarks).
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Create per-user inbox records so recipients can retrieve missed notifications
        var userNotificationRepo = unitOfWork.GetRepository<UserNotification, UserNotificationIdentifierType>();
        foreach (var recipientId in recipientIds)
        {
            var userNotification = UserNotification.Create(recipientId, notification.Id).Value!;
            await userNotificationRepo.AddAsync(userNotification, cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Send the notification via SignalR (or other configured sender). The live leg carries the
        // scope key, so a client viewing another scope can leave it out of its toast and badge.
        try
        {
            await pushNotificationSender.SendToUsersAsync(
                recipientIds,
                command.Request.Title,
                command.Request.Body,
                LiveMetadata(notification.ScopeKey),
                cancellationToken).ConfigureAwait(false);

            notification.MarkAsSent();
            LogNotificationSent(logger, notification.Id, recipientIds.Count);
        }
#pragma warning disable CA1031 // Do not catch general exception types — delivery failure is non-fatal; we record the status
        catch (Exception ex)
#pragma warning restore CA1031
        {
            notification.MarkAsFailed();
            LogNotificationFailed(logger, notification.Id, ex);
        }

        // Third channel (ADR-044): OS-level push through the native sender, reaching devices the
        // SignalR hub cannot (app backgrounded or killed). Best-effort by design — the SignalR leg
        // above already decided the audit status, and the default NullNativePushSender keeps this
        // a no-op until a notification hub is configured.
        try
        {
            await nativePushSender.SendToUsersAsync(
                recipientIds,
                command.Request.Title,
                command.Request.Body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types — native delivery is best-effort
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNativePushFailed(logger, notification.Id, ex);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(dtoMapper.MapToDTO(notification));
    }

    /// <summary>
    /// Derives the stored deduplication key from the sender and the client key: a hex SHA-256, so it
    /// is always 64 characters (within the column) whatever the client sent.
    /// </summary>
    /// <param name="sentByUserId">The sending user.</param>
    /// <param name="clientKey">The client-supplied key.</param>
    /// <returns>The sender-scoped key.</returns>
    private static string SenderScopedDedupKey(UserIdentifierType sentByUserId, string clientKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{sentByUserId}:{clientKey}"))));

    /// <summary>
    /// The live leg's metadata: the scope key under <see cref="NotificationScopeKey.MetadataKey"/> for
    /// a scoped send, and no metadata at all for an unscoped one.
    /// </summary>
    /// <param name="scopeKey">The notification's (normalized) scope key, or null when unscoped.</param>
    /// <returns>The metadata, or null.</returns>
    private static Dictionary<string, string>? LiveMetadata(string? scopeKey) =>
        scopeKey is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { [NotificationScopeKey.MetadataKey] = scopeKey };

    /// <summary>
    /// Looks up an already-persisted notification by its deduplication key. Uses the read
    /// repository from the unit of work (never an injected <c>IRepository</c>), so the lookup runs
    /// against the same data source as the write above.
    /// </summary>
    private async Task<PushNotification?> FindByDedupKeyAsync(string dedupKey, CancellationToken cancellationToken)
    {
        var readRepository = unitOfWork.GetReadRepository<PushNotification, PushNotificationIdentifierType>();
        IReadOnlyCollection<PushNotification> matches = await readRepository.GetAllAsync(
            [],
            where: n => n.DedupKey == dedupKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return matches.FirstOrDefault();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push notification {NotificationId} sent to {RecipientCount} recipients")]
    private static partial void LogNotificationSent(ILogger logger, PushNotificationIdentifierType notificationId, int recipientCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Push notification {NotificationId} delivery failed")]
    private static partial void LogNotificationFailed(ILogger logger, PushNotificationIdentifierType notificationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Push notification {NotificationId} native (OS-level) delivery failed; inbox and SignalR legs unaffected")]
    private static partial void LogNativePushFailed(ILogger logger, PushNotificationIdentifierType notificationId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Push notification {NotificationId} already exists for dedup key {DedupKey}; returning it without sending again")]
    private static partial void LogDedupHit(ILogger logger, PushNotificationIdentifierType notificationId, string dedupKey);
}
